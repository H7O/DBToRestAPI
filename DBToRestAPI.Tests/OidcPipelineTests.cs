using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace DBToRestAPI.Tests;

/// <summary>
/// An OIDC provider answered by <see cref="FakeHttpServer"/>: a discovery document, its signing key
/// and a UserInfo endpoint, and tokens it signs for the tests to call the engine with.
/// </summary>
public sealed class FakeIdp
{
    private readonly RsaSecurityKey _key = NewKey("key-1");
    private readonly string _jwksPath;

    public string Issuer { get; }
    public string Audience { get; }
    public string DiscoveryUrl => Issuer + "/.well-known/openid-configuration";
    public string JwksUrl => Issuer + _jwksPath;
    public string UserInfoUrl => Issuer + "/userinfo";

    public FakeIdp(FakeHttpServer server, string issuer, string audience, string jwksPath = "/jwks")
    {
        Issuer = issuer;
        Audience = audience;
        _jwksPath = jwksPath;

        server.Map(DiscoveryUrl, _ => FakeHttpServer.Json(new
        {
            issuer = Issuer,
            jwks_uri = JwksUrl,
            userinfo_endpoint = UserInfoUrl,
            authorization_endpoint = Issuer + "/authorize",
            token_endpoint = Issuer + "/token",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        }));

        server.Map(JwksUrl, _ => KeySet());

        server.Map(UserInfoUrl, _ => FakeHttpServer.Json(new
        {
            sub = "userinfo-subject",
            given_name = "Grace",
            family_name = "Hopper",
            email = "grace@example.com",
            email_verified = true,
        }));
    }

    public static RsaSecurityKey NewKey(string keyId) => new(RSA.Create(2048)) { KeyId = keyId };

    /// <summary>The provider's key set, as its JWKS URL answers it.</summary>
    public HttpResponseMessage KeySet()
    {
        var key = _key.Rsa.ExportParameters(false);
        return FakeHttpServer.Json(new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = _key.KeyId,
                      n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) },
            },
        });
    }

    /// <summary>
    /// The claims of a full token, as Entra ID sends them, less any named in <paramref name="omit"/>.
    /// Each token gets its own jti, so two tokens are never the same text.
    /// </summary>
    public static Dictionary<string, object> Claims(params string[] omit)
    {
        var claims = new Dictionary<string, object>
        {
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["sub"] = "token-subject",
            ["oid"] = "object-id",
            ["tid"] = "tenant-id",
            ["name"] = "Ada Lovelace",
            ["given_name"] = "Ada",
            ["family_name"] = "Lovelace",
            ["email"] = "ada@example.com",
            ["roles"] = new[] { "admin", "reader" },
        };
        foreach (var name in omit)
            claims.Remove(name);
        return claims;
    }

    /// <summary>A token signed with this provider's key; <paramref name="edit"/> can change anything.</summary>
    public string Issue(Dictionary<string, object> claims, Action<SecurityTokenDescriptor>? edit = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(30),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        };
        edit?.Invoke(descriptor);
        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }
}

/// <summary>
/// The whole engine validating real signed tokens against a provider answered in process: the
/// discovery document and signing keys come through the engine's OIDC metadata client, UserInfo
/// through its own. What used to need a local OIDC server and a live engine.
/// </summary>
public class OidcPipelineTests(OidcPipelineTests.OidcEngine engine) : IClassFixture<OidcPipelineTests.OidcEngine>
{
    public sealed class OidcEngine : PipelineTests.PipelineEngine
    {
        public FakeIdp Idp { get; }
        public FakeIdp Idp2 { get; }

        // Its JWKS URL answers a maintenance page, then a key set with no key, then its keys.
        public FakeIdp Flaky { get; }

        // Its JWKS URL has a query string, as an Azure AD B2C policy's does.
        public FakeIdp Policy { get; }

        // As Policy, but its JWKS URL answers 503.
        public FakeIdp Down { get; }

        public OidcEngine() : base(_ => Settings())
        {
            Idp = new FakeIdp(Http, "https://idp.test", "api://pipeline");
            Idp2 = new FakeIdp(Http, "https://idp2.test", "api://pipeline2");
            Flaky = new FakeIdp(Http, "https://flaky.test", "api://pipeline", "/keys?p=SECRET-FLAKY");
            Policy = new FakeIdp(Http, "https://policy.test", "api://pipeline", "/keys?p=SECRET-POLICY");
            Down = new FakeIdp(Http, "https://down.test", "api://pipeline", "/keys?p=SECRET-DOWN");
            Http.Map(Down.JwksUrl, _ => FakeHttpServer.Text("unavailable", status: HttpStatusCode.ServiceUnavailable));
            var keySetCalls = 0;
            Http.Map(Flaky.JwksUrl, _ => Interlocked.Increment(ref keySetCalls) switch
            {
                1 => FakeHttpServer.Text("<html>maintenance</html>", "text/html"),
                2 => FakeHttpServer.Json(new { keys = Array.Empty<object>() }),
                _ => Flaky.KeySet(),
            });
        }

        private static Dictionary<string, string?> Settings() => new()
        {
            ["authorize:providers:idp:authority"] = "https://idp.test",
            ["authorize:providers:idp:audience"] = "api://pipeline",
            ["authorize:providers:idp:clock_skew_seconds"] = "60",
            ["authorize:providers:idp:userinfo_fallback_claims"] = "email,name,given_name,family_name",
            ["authorize:providers:idp:userinfo_cache_duration_seconds"] = "300",

            ["authorize:providers:idp2:authority"] = "https://idp2.test",
            ["authorize:providers:idp2:audience"] = "api://pipeline2",

            // Discovery over plain http is refused before any request is made.
            ["authorize:providers:plain_http:authority"] = "http://plain.test",
            ["authorize:providers:plain_http:audience"] = "api://pipeline",

            ["queries:auth_claims:route"] = "auth/claims",
            ["queries:auth_claims:verb"] = "GET",
            ["queries:auth_claims:authorize:provider"] = "idp",
            ["queries:auth_claims:query"] =
                "SELECT {auth{user_id}} AS user_id, {auth{sub}} AS sub, {auth{given_name}} AS given_name, "
                + "{auth{family_name}} AS family_name, {auth{oid}} AS oid, {auth{tid}} AS tid, {auth{scp}} AS scp, "
                + "{auth{email}} AS email, {auth{email_verified}} AS email_verified, {auth{roles}} AS roles, "
                + "{auth{auth_provider}} AS provider;",

            ["queries:auth_scoped:route"] = "auth/scoped",
            ["queries:auth_scoped:verb"] = "GET",
            ["queries:auth_scoped:authorize:provider"] = "idp",
            ["queries:auth_scoped:authorize:required_scopes"] = "files.read",
            ["queries:auth_scoped:query"] = "SELECT 'ok' AS result;",

            ["queries:auth_roles:route"] = "auth/roles",
            ["queries:auth_roles:verb"] = "GET",
            ["queries:auth_roles:authorize:provider"] = "idp",
            ["queries:auth_roles:authorize:required_roles"] = "admin",
            ["queries:auth_roles:query"] = "SELECT 'ok' AS result;",

            ["queries:auth_multi:route"] = "auth/multi",
            ["queries:auth_multi:verb"] = "GET",
            ["queries:auth_multi:authorize:provider"] = "idp,idp2",
            ["queries:auth_multi:query"] = "SELECT {auth{auth_provider}} AS provider, {auth{user_id}} AS user_id;",

            ["queries:auth_plain_http:route"] = "auth/plain_http",
            ["queries:auth_plain_http:verb"] = "GET",
            ["queries:auth_plain_http:authorize:provider"] = "plain_http",
            ["queries:auth_plain_http:query"] = "SELECT 1 AS a;",

            ["authorize:providers:down:authority"] = "https://down.test",
            ["authorize:providers:down:audience"] = "api://pipeline",
            ["queries:auth_down:route"] = "auth/down",
            ["queries:auth_down:verb"] = "GET",
            ["queries:auth_down:authorize:provider"] = "down",
            ["queries:auth_down:query"] = "SELECT 'ok' AS result;",

            ["authorize:providers:policy:authority"] = "https://policy.test",
            ["authorize:providers:policy:audience"] = "api://pipeline",
            ["queries:auth_policy:route"] = "auth/policy",
            ["queries:auth_policy:verb"] = "GET",
            ["queries:auth_policy:authorize:provider"] = "policy",
            ["queries:auth_policy:query"] = "SELECT 'ok' AS result;",

            ["authorize:providers:flaky:authority"] = "https://flaky.test",
            ["authorize:providers:flaky:audience"] = "api://pipeline",
            ["queries:auth_flaky:route"] = "auth/flaky",
            ["queries:auth_flaky:verb"] = "GET",
            ["queries:auth_flaky:authorize:provider"] = "flaky",
            ["queries:auth_flaky:query"] = "SELECT 'ok' AS result;",
        };
    }

    private FakeHttpServer Http => engine.Http;

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(
        string path, string token, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        using var response = await engine.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string? Text(JsonElement row, string name)
        => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).ToString();

    [Fact]
    public async Task FullToken_GivesEveryAuthName_WithoutCallingUserInfo()
    {
        var claims = FakeIdp.Claims();
        claims["scp"] = "files.read";
        var calls = Http.Count(engine.Idp.UserInfoUrl);

        var (status, row) = await GetAsync("auth/claims", engine.Idp.Issue(claims));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("token-subject", Text(row, "user_id"));
        Assert.Equal("token-subject", Text(row, "sub"));
        Assert.Equal("Ada", Text(row, "given_name"));
        Assert.Equal("Lovelace", Text(row, "family_name"));
        Assert.Equal("object-id", Text(row, "oid"));
        Assert.Equal("tenant-id", Text(row, "tid"));
        Assert.Equal("files.read", Text(row, "scp"));
        Assert.Equal("ada@example.com", Text(row, "email"));
        Assert.Equal("admin|reader", Text(row, "roles"));
        Assert.Equal("idp", Text(row, "provider"));
        Assert.Equal(calls, Http.Count(engine.Idp.UserInfoUrl));
    }

    [Fact]
    public async Task UserInfo_FillsOnlyWhatTheTokenLacks_AndIsCalledOncePerToken()
    {
        var token = engine.Idp.Issue(FakeIdp.Claims("email"));
        var calls = Http.Count(engine.Idp.UserInfoUrl);

        var (status, row) = await GetAsync("auth/claims", token);
        await GetAsync("auth/claims", token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("grace@example.com", Text(row, "email"));      // from UserInfo
        Assert.Equal("true", Text(row, "email_verified"));          // a JSON true, as text
        Assert.Equal("token-subject", Text(row, "sub"));            // the token's, not UserInfo's
        Assert.Equal("Ada", Text(row, "given_name"));
        Assert.Equal(calls + 1, Http.Count(engine.Idp.UserInfoUrl));
        var call = Http.Requests.Last(r => r.Uri.AbsoluteUri == engine.Idp.UserInfoUrl);
        Assert.Equal("Bearer " + token, call.Authorization);
    }

    [Fact]
    public async Task TokenEmails_BeatsTheUserInfoEmail()
    {
        // Azure AD B2C sends `emails` instead of `email`.
        var claims = FakeIdp.Claims("email", "name");
        claims["emails"] = "token@example.com";

        var (status, row) = await GetAsync("auth/claims", engine.Idp.Issue(claims));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("token@example.com", Text(row, "email"));
    }

    [Fact]
    public async Task RequiredScopes_AcceptsTheScopeAndRejectsItsAbsence()
    {
        var withScope = FakeIdp.Claims();
        withScope["scp"] = "files.read";

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("auth/scoped", engine.Idp.Issue(withScope))).Status);

        var (status, body) = await GetAsync("auth/scoped", engine.Idp.Issue(FakeIdp.Claims()));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("Insufficient permissions", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ScopesAsAnArray_AreAllRead()
    {
        // Okta sends scp as a JSON array.
        var claims = FakeIdp.Claims();
        claims["scp"] = new[] { "openid", "files.read" };
        var token = engine.Idp.Issue(claims);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("auth/scoped", token)).Status);
        var (_, row) = await GetAsync("auth/claims", token);
        Assert.Equal("openid files.read", Text(row, "scp"));
    }

    [Fact]
    public async Task RequiredRoles_AcceptsTheRoleAndRejectsItsAbsence()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("auth/roles", engine.Idp.Issue(FakeIdp.Claims()))).Status);

        var readerOnly = FakeIdp.Claims();
        readerOnly["roles"] = new[] { "reader" };
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("auth/roles", engine.Idp.Issue(readerOnly))).Status);
    }

    public static TheoryData<string, string> RejectedTokens => new()
    {
        { "expired", "Token has expired" },
        { "other audience", "Invalid token" },
        { "other issuer", "Invalid token" },
        { "other key", "Invalid token signature" },
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task InvalidToken_Is401(string problem, string message)
    {
        var now = DateTime.UtcNow;
        var token = engine.Idp.Issue(FakeIdp.Claims(), d =>
        {
            switch (problem)
            {
                case "expired":                      // beyond the provider's 60 s of clock skew
                    d.IssuedAt = d.NotBefore = now.AddMinutes(-20);
                    d.Expires = now.AddMinutes(-10);
                    break;
                case "other audience":
                    d.Audience = "api://someone-else";
                    break;
                case "other issuer":
                    d.Issuer = "https://someone-else.test";
                    break;
                case "other key":                    // the provider's key id, but not its key
                    d.SigningCredentials = new SigningCredentials(FakeIdp.NewKey("key-1"), SecurityAlgorithms.RsaSha256);
                    break;
            }
        });

        var (status, body) = await GetAsync("auth/claims", token);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(message, body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task DiscoveryAndKeys_AreFetchedOnce()
    {
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await GetAsync("auth/roles", engine.Idp.Issue(FakeIdp.Claims()))).Status);

        Assert.Equal(1, Http.Count(engine.Idp.DiscoveryUrl));
        Assert.Equal(1, Http.Count(engine.Idp.JwksUrl));
    }

    [Fact]
    public async Task AKeySetThatIsntOne_IsNotCached_SoTheNextRequestFetchesAgain()
    {
        var token = engine.Flaky.Issue(FakeIdp.Claims());

        Assert.Equal(HttpStatusCode.InternalServerError, (await GetAsync("auth/flaky", token)).Status);  // a maintenance page
        Assert.Equal(HttpStatusCode.InternalServerError, (await GetAsync("auth/flaky", token)).Status);  // no signing key
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("auth/flaky", token)).Status);                   // the provider recovered
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("auth/flaky", token)).Status);

        Assert.Equal(3, Http.Count(engine.Flaky.JwksUrl));   // then cached
        Assert.Contains(engine.Logs.Entries, e => e.Text.Contains("No signing keys in the JWKS at https://flaky.test/keys"));
        Assert.DoesNotContain(engine.Logs.Entries, e => e.Text.Contains("SECRET-FLAKY"));
    }

    [Fact]
    public async Task AKeySetUrlsQueryString_NeverReachesTheLog()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("auth/policy", engine.Policy.Issue(FakeIdp.Claims()))).Status);

        Assert.Equal("?p=SECRET-POLICY", Http.Requests.Last(r => r.Uri.Host == "policy.test" && r.Uri.AbsolutePath == "/keys").Uri.Query);
        Assert.Contains(engine.Logs.Entries, e => e.Message.Contains("https://policy.test/keys"));
        Assert.DoesNotContain(engine.Logs.Entries, e => e.Text.Contains("SECRET-POLICY"));
    }

    [Fact]
    public async Task AKeySetUrlThatFails_IsLoggedWithoutItsQueryString()
    {
        Assert.Equal(HttpStatusCode.InternalServerError, (await GetAsync("auth/down", engine.Down.Issue(FakeIdp.Claims()))).Status);

        Assert.Contains(engine.Logs.Entries, e => e.Text.Contains("Unable to retrieve https://down.test/keys: the provider answered 503 ServiceUnavailable"));
        Assert.DoesNotContain(engine.Logs.Entries, e => e.Text.Contains("SECRET-DOWN"));
    }

    [Fact]
    public async Task AuthorityNotHttps_IsRefusedWithoutARequest()
    {
        var (status, _) = await GetAsync("auth/plain_http", engine.Idp.Issue(FakeIdp.Claims()));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.DoesNotContain(Http.Requests, r => r.Uri.Host == "plain.test");
        Assert.Contains(engine.Logs.Entries, e => e.Text.Contains(
            "Unable to retrieve http://plain.test/.well-known/openid-configuration: the address must be https"));
    }

    [Fact]
    public async Task MultiProvider_PicksTheTokensIssuer()
    {
        var (status, row) = await GetAsync("auth/multi", engine.Idp2.Issue(FakeIdp.Claims()));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("idp2", Text(row, "provider"));

        (status, row) = await GetAsync("auth/multi", engine.Idp.Issue(FakeIdp.Claims()));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("idp", Text(row, "provider"));
    }

    [Fact]
    public async Task MultiProvider_AHintNamingAnotherProvider_Is401()
    {
        // The hint selects idp, whose key and issuer the token from idp2 doesn't match.
        var (status, _) = await GetAsync("auth/multi", engine.Idp2.Issue(FakeIdp.Claims()), ("X-Auth-Provider", "idp"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task MultiProvider_AnIssuerWithALineBreak_StaysOnOneLogLine()
    {
        // The issuer is read before the token is validated, so it is the caller's text.
        var token = engine.Idp.Issue(FakeIdp.Claims(), d => d.Issuer = "https://nobody.test\nforged line");

        var (status, _) = await GetAsync("auth/multi", token);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        var entry = Assert.Single(engine.Logs.Entries, e => e.Message.Contains("https://nobody.test"));
        Assert.Contains("https://nobody.test%0Aforged line", entry.Message);
        Assert.DoesNotContain('\n', entry.Message);
    }

    [Fact]
    public async Task MultiProvider_AHintWithControlCharacters_StaysOnOneLogLine()
    {
        // The server accepts a vertical tab and U+2028 in a header value; both break a log line.
        var context = await engine.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/auth/multi";
            c.Request.Headers.Authorization = "Bearer " + engine.Idp.Issue(FakeIdp.Claims());
            c.Request.Headers["X-Auth-Provider"] = "nope\u2028forged\u000Bline";
        });

        Assert.Equal(401, context.Response.StatusCode);
        var entry = Assert.Single(engine.Logs.Entries, e => e.Message.Contains("Provider hint 'nope"));
        Assert.Contains("Provider hint 'nope%E2%80%A8forged%0Bline'", entry.Message);
    }

    [Fact]
    public async Task SingleProvider_ATokenFromAnotherProvider_Is401()
    {
        var (status, _) = await GetAsync("auth/claims", engine.Idp2.Issue(FakeIdp.Claims()));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}
