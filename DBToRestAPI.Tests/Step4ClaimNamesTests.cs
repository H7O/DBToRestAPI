using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using DBToRestAPI.Middlewares;
using Microsoft.IdentityModel.Tokens;

namespace DBToRestAPI.Tests;

/// <summary>
/// The claims a query sees through {auth{...}}, and the scope check, with the claim renaming
/// that JwtSecurityTokenHandler really does.
///
/// .NET renames well-known claims on the way in: sub becomes ClaimTypes.NameIdentifier, scp
/// becomes http://schemas.microsoft.com/identity/claims/scope, oid, tid, given_name,
/// family_name and email get long names too. Before 1.7.6:
/// - required_scopes looked only for "scp" and "scope", so every Entra or Okta token got 403;
/// - {auth{sub}}, {auth{given_name}} and {auth{family_name}} were missing;
/// - the UserInfo fallback thought given_name and family_name were always missing, and the
///   claims it added were JsonElement values that no SQL driver can bind (a generic 400).
/// Each test validates a real signed token, so the renaming is the handler's, not a guess.
/// </summary>
public class Step4ClaimNamesTests
{
    private static ClaimsPrincipal Validate(Dictionary<string, object> claims)
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var token = new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });

        // A new handler with its defaults, exactly as the middleware validates.
        return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            IssuerSigningKey = key,
            ValidateIssuer = false,
            ValidateAudience = false,
        }, out _);
    }

    private static ClaimsPrincipal EntraLikeToken() => Validate(new()
    {
        ["sub"] = "pairwise-subject",
        ["oid"] = "object-id",
        ["tid"] = "tenant-id",
        ["scp"] = "files.read files.write",
        ["given_name"] = "Ada",
        ["family_name"] = "Lovelace",
        ["email"] = "ada@example.com",
        ["roles"] = new[] { "admin", "reader" },
    });

    [Fact]
    public void TheHandlerReallyRenamesTheseClaims()
    {
        // Guards the premise: if a future handler stops renaming, these tests must be revisited.
        var principal = EntraLikeToken();

        Assert.Null(principal.FindFirst("sub"));
        Assert.Null(principal.FindFirst("scp"));
        Assert.Null(principal.FindFirst("given_name"));
        Assert.NotNull(principal.FindFirst(ClaimTypes.NameIdentifier));
        Assert.NotNull(principal.FindFirst("http://schemas.microsoft.com/identity/claims/scope"));
    }

    [Fact]
    public void Scopes_AreFoundUnderTheRenamedScpClaim()
    {
        var scopes = Step4JwtAuthorization.GetScopes(EntraLikeToken());

        Assert.Contains("files.read", scopes);
        Assert.Contains("files.write", scopes);
    }

    [Fact]
    public void Scopes_FromAScopeClaim_StillWork()
    {
        var scopes = Step4JwtAuthorization.GetScopes(Validate(new() { ["sub"] = "u", ["scope"] = "openid profile" }));

        Assert.Equal(new HashSet<string> { "openid", "profile" }, scopes);
    }

    /// <summary>
    /// Runs the middleware's claims sequence with a stand-in for the UserInfo endpoint, and
    /// counts the calls.
    /// </summary>
    private static async Task<(Dictionary<string, object> Claims, Step4JwtAuthorization.TokenIdentity Token, int UserInfoCalls)> ResolveAsync(
        ClaimsPrincipal principal, string? userInfoJson = null, string fallbackClaims = "email,name,given_name,family_name")
    {
        var calls = 0;
        var (claims, token) = await Step4JwtAuthorization.ResolveClaimsAsync(principal, fallbackClaims, "entra", _ =>
        {
            calls++;
            return Task.FromResult(userInfoJson is null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(userInfoJson));
        });
        return (claims, token, calls);
    }

    [Fact]
    public async Task ClaimsDictionary_ExposesTheTokensOwnNames()
    {
        var (claims, _, _) = await ResolveAsync(EntraLikeToken());

        Assert.Equal("pairwise-subject", claims["user_id"]);
        Assert.Equal("pairwise-subject", claims["sub"]);
        Assert.Equal("object-id", claims["oid"]);
        Assert.Equal("tenant-id", claims["tid"]);
        Assert.Equal("files.read files.write", claims["scp"]);
        Assert.Equal("Ada", claims["given_name"]);
        Assert.Equal("Lovelace", claims["family_name"]);
        Assert.Equal("ada@example.com", claims["email"]);
        Assert.Equal("admin|reader", claims["roles"]);
        Assert.Equal("entra", claims["auth_provider"]);
        // The long names stay available as before.
        Assert.Equal("pairwise-subject", claims[ClaimTypes.NameIdentifier]);
        Assert.Equal("Ada", claims[ClaimTypes.GivenName]);
    }

    [Fact]
    public void UserId_FallsBackToTheRenamedOid()
    {
        var principal = Validate(new() { ["oid"] = "object-id", ["name"] = "No Subject" });

        Assert.Equal("object-id",
            Step4JwtAuthorization.FirstClaimValue(principal, ClaimTypes.NameIdentifier, "sub", "oid"));
    }

    [Fact]
    public async Task UserInfo_IsNotCalled_WhenTheTokenHasEveryListedClaim()
    {
        // given_name and family_name are renamed by .NET; looked up by their token names only,
        // they always seemed missing and UserInfo was called for every new token.
        var (_, _, calls) = await ResolveAsync(Validate(new()
        {
            ["sub"] = "u", ["name"] = "Ada Lovelace", ["email"] = "ada@example.com",
            ["given_name"] = "Ada", ["family_name"] = "Lovelace",
        }), userInfoJson: "{}");

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task UserInfo_IsCalled_WhenAListedClaimIsMissing()
    {
        var (_, _, calls) = await ResolveAsync(Validate(new() { ["sub"] = "u", ["name"] = "Ada" }), userInfoJson: "{}");

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UserInfo_CannotGrantScopesOrRoles()
    {
        // The role and scope checks use the token's identity, read before the merge. The old
        // code read scopes after it, so a UserInfo `scope` satisfied required_scopes.
        var (claims, token, calls) = await ResolveAsync(
            Validate(new() { ["sub"] = "u" }),
            userInfoJson: """{ "scope": "admin.all", "scp": "admin.all", "roles": ["admin"] }""");

        Assert.Equal(1, calls);
        Assert.Empty(token.Scopes);
        Assert.Empty(token.Roles);
        Assert.Equal("u", claims["user_id"]);
    }

    [Fact]
    public async Task Email_PrefersTheTokensEmailsClaim_OverUserInfo()
    {
        // Azure AD B2C sends the address as "emails", which .NET doesn't rename. A UserInfo
        // "email" must not replace it.
        var (claims, _, _) = await ResolveAsync(
            Validate(new() { ["sub"] = "u", ["emails"] = "token@example.com" }),
            userInfoJson: """{ "email": "userinfo@example.com", "name": "From UserInfo" }""");

        Assert.Equal("token@example.com", claims["email"]);
        Assert.Equal("From UserInfo", claims["name"]); // the token had no name, so UserInfo fills it
    }

    [Fact]
    public async Task Scp_AsAnArray_HoldsEveryScope()
    {
        // Okta sends scp as a JSON array, one claim per scope.
        var (claims, token, _) = await ResolveAsync(Validate(new()
        {
            ["sub"] = "u", ["name"] = "n", ["email"] = "e@example.com", ["given_name"] = "g", ["family_name"] = "f",
            ["scp"] = new[] { "openid", "orders.write" },
        }));

        Assert.Equal("openid orders.write", claims["scp"]);
        Assert.Equal("openid orders.write", claims["http://schemas.microsoft.com/identity/claims/scope"]);
        Assert.Contains("orders.write", token.Scopes);
    }

    [Fact]
    public async Task UserInfoClaims_AreStrings_AndNeverReplaceTheTokensClaims()
    {
        // GetUserInfoAsync deserialises into Dictionary<string, object>: every value is a JsonElement.
        var (claims, _, calls) = await ResolveAsync(Validate(new() { ["sub"] = "token-subject", ["scp"] = "files.read" }), """
            {
              "sub": "userinfo-subject",
              "given_name": "Grace",
              "email": "grace@example.com",
              "email_verified": true,
              "updated_at": 1700000000,
              "address": { "country": "NZ" },
              "nickname": null
            }
            """);

        Assert.Equal(1, calls);
        // Every value a query can bind is a plain string.
        Assert.All(claims.Values, v => Assert.IsType<string>(v));
        // The signed token's subject wins over the UserInfo one.
        Assert.Equal("token-subject", claims["sub"]);
        Assert.Equal("token-subject", claims["user_id"]);
        // Claims the token lacked come from UserInfo.
        Assert.Equal("Grace", claims["given_name"]);
        Assert.Equal("grace@example.com", claims["email"]);
        Assert.Equal("true", claims["email_verified"]);
        Assert.Equal("1700000000", claims["updated_at"]);
        Assert.Equal("""{ "country": "NZ" }""", claims["address"]);
        Assert.False(claims.ContainsKey("nickname"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("text", "text")]
    [InlineData(42, "42")]
    public void ClaimValueToString_PlainValues(object? value, string? expected)
    {
        Assert.Equal(expected, Step4JwtAuthorization.ClaimValueToString(value));
    }
}
