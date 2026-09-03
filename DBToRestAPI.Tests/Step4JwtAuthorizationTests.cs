using DBToRestAPI.Cache;
using DBToRestAPI.Middlewares;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DBToRestAPI.Tests;

/// <summary>
/// Regression tests for <see cref="Step4JwtAuthorization"/> bearer-token classification.
///
/// Focus: a bearer that is NOT a parseable JWT must be rejected as 401 ("Invalid token"), not 500.
/// Before the fix, the multi-provider provider-hint path sent such a token straight into
/// JwtSecurityTokenHandler.ValidateToken, which throws ArgumentException (IDX12741, wrong segment count)
/// — not a SecurityTokenException — so it fell through the catch ladder to the generic 500. A guard placed
/// before the discovery fetch now returns 401 up front, matching the issuer-fallback path. The guard parses
/// the header and payload (TryReadJwt) rather than relying on CanReadToken, which only checks for three
/// base64url segments and let correctly-shaped garbage such as "abc.def.ghi" through to the same 500.
///
/// These tests are fully offline: an unreadable token is rejected before any OIDC discovery/JWKS fetch,
/// so no network (and no mocked discovery document) is required.
/// </summary>
public class Step4JwtAuthorizationTests
{
    private const string UnparseableBearer = "Bearer not.a.real.token"; // 4 segments => not a JWT

    private static IConfigurationRoot BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Configured OIDC providers (global) — needed so the hint/allow-list resolves.
            ["authorize:providers:google:authority"] = "https://accounts.google.com",
            ["authorize:providers:google:audience"] = "google-client-id",
            ["authorize:providers:auth0:authority"] = "https://example.auth0.com/",
            ["authorize:providers:auth0:audience"] = "https://api.example.com",

            // A multi-provider endpoint (google,auth0) with a hint header.
            ["queries:multi:authorize:enabled"] = "true",
            ["queries:multi:authorize:provider"] = "google,auth0",
            ["queries:multi:authorize:provider_hint_header"] = "X-Auth-Provider",

            // A single-provider endpoint (fast path).
            ["queries:single:authorize:enabled"] = "true",
            ["queries:single:authorize:provider"] = "google",
        })
        .Build();

    private sealed class Harness
    {
        public Step4JwtAuthorization Middleware = null!;
        public DefaultHttpContext Context = new();
        public bool NextCalled;

        public int Status => Context.Response.StatusCode;
    }

    private static Harness CreateHarness(string endpointKey, string authorization, params (string name, string value)[] headers)
    {
        var configRoot = BuildConfig();
        var encrypted = new TestEncryptedConfiguration(configRoot);

        // CacheService needs a real HybridCache; the bad-token path never uses it (guard fires first),
        // but it is a constructor dependency, so spin up a minimal container to satisfy it.
        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var cacheService = new CacheService(encrypted, sp, sp.GetRequiredService<HybridCache>());

        var harness = new Harness();
        RequestDelegate next = _ => { harness.NextCalled = true; return Task.CompletedTask; };

        harness.Middleware = new Step4JwtAuthorization(
            next,
            NullLogger<Step4JwtAuthorization>.Instance,
            cacheService,
            encrypted,
            Mock.Of<IHttpClientFactory>(),     // never invoked on the unreadable-token path
            new OidcProviderIndex(encrypted));

        harness.Context.Items["section"] = configRoot.GetSection($"queries:{endpointKey}");
        harness.Context.Items["route"] = endpointKey;
        harness.Context.Request.Headers.Authorization = authorization;
        foreach (var (name, value) in headers)
            harness.Context.Request.Headers[name] = value;
        harness.Context.Response.Body = new MemoryStream();

        return harness;
    }

    [Fact]
    public async Task MultiProvider_WithHint_UnparseableBearer_Returns401_Not500()
    {
        // The regression: provider hint + a non-JWT bearer used to return 500.
        var h = CreateHarness("multi", UnparseableBearer, ("X-Auth-Provider", "google"));

        await h.Middleware.InvokeAsync(h.Context);

        Assert.Equal(401, h.Status);        Assert.False(h.NextCalled); // request must not reach the controller
    }

    [Fact]
    public async Task MultiProvider_NoHint_UnparseableBearer_Returns401()
    {
        // Issuer-fallback path: no hint + unreadable token => provider unresolved => 401 (already correct).
        var h = CreateHarness("multi", UnparseableBearer);

        await h.Middleware.InvokeAsync(h.Context);

        Assert.Equal(401, h.Status);        Assert.False(h.NextCalled);
    }

    [Fact]
    public async Task SingleProvider_UnparseableBearer_Returns401()
    {
        // The same guard protects the single-provider fast path.
        var h = CreateHarness("single", UnparseableBearer);

        await h.Middleware.InvokeAsync(h.Context);

        Assert.Equal(401, h.Status);        Assert.False(h.NextCalled);
    }

    [Fact]
    public async Task MissingAuthorizationHeader_Returns401()
    {
        // Sanity: no Authorization header is still 401 (not affected by the guard).
        var h = CreateHarness("multi", authorization: "", ("X-Auth-Provider", "google"));

        await h.Middleware.InvokeAsync(h.Context);

        Assert.Equal(401, h.Status);
        Assert.False(h.NextCalled);
    }

    /// <summary>
    /// Shape alone is not enough. CanReadToken accepts anything with three base64url segments, so
    /// "abc.def.ghi" passed the original guard and blew up inside ValidateToken with a 500. The guard
    /// now parses the header and payload (TryReadJwt), so correctly-shaped garbage is a 401 as well.
    /// </summary>
    [Theory]
    [InlineData("multi", "notajwt")]                              // 1 segment
    [InlineData("multi", "a.b")]                                  // 2 segments
    [InlineData("multi", "....")]                                 // 5 segments
    [InlineData("multi", "abc.def.ghi")]                          // 3 segments, not base64url JSON
    [InlineData("multi", "eyJhbGciOiJIUzI1NiJ9.bm90anNvbg.sig")]  // valid header, payload "notjson"
    [InlineData("single", "abc.def.ghi")]
    [InlineData("single", "eyJhbGciOiJIUzI1NiJ9.bm90anNvbg.sig")]
    public async Task MalformedBearer_AnyShape_Returns401_Not500(string endpointKey, string token)
    {
        var h = endpointKey == "multi"
            ? CreateHarness(endpointKey, "Bearer " + token, ("X-Auth-Provider", "google"))
            : CreateHarness(endpointKey, "Bearer " + token);

        await h.Middleware.InvokeAsync(h.Context);

        Assert.Equal(401, h.Status);
        Assert.False(h.NextCalled);
    }
}
