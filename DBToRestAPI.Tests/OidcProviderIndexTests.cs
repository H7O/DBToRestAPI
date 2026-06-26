using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Tests;

/// <summary>
/// Tests for <see cref="OidcProviderIndex"/> — the multi-provider OIDC routing index.
/// Reuses the in-memory <c>TestEncryptedConfiguration</c> helper (defined in
/// QueryRouteResolverTests.cs) to simulate the IConfiguration tree produced by the XML provider.
/// </summary>
public class OidcProviderIndexTests
{
    private static OidcProviderIndex BuildIndex(Dictionary<string, string?> data)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();
        return new OidcProviderIndex(new TestEncryptedConfiguration(config));
    }

    private static Dictionary<string, string?> ThreeProviders() => new()
    {
        // Google: no explicit <issuer> -> expected issuer falls back to <authority>
        ["authorize:providers:google:authority"] = "https://accounts.google.com",
        ["authorize:providers:google:audience"] = "google-client-id",

        // Azure B2C: explicit <issuer> differs from <authority>
        ["authorize:providers:azure_b2c:authority"] = "https://b2c.example.com/tenant",
        ["authorize:providers:azure_b2c:issuer"] = "https://b2c.example.com/guid/v2.0/",
        ["authorize:providers:azure_b2c:audience"] = "b2c-app-id",

        ["authorize:providers:auth0:authority"] = "https://example.auth0.com/",
        ["authorize:providers:auth0:audience"] = "https://api.example.com",
    };

    // ── ExpandAllowedProviders ───────────────────────────────────────────────

    [Fact]
    public void Expand_SingleProvider_ReturnsOne()
    {
        var index = BuildIndex(ThreeProviders());
        Assert.Equal(new[] { "google" }, index.ExpandAllowedProviders("google"));
    }

    [Fact]
    public void Expand_CommaList_TrimsAndPreservesOrder()
    {
        var index = BuildIndex(ThreeProviders());
        Assert.Equal(new[] { "google", "azure_b2c" }, index.ExpandAllowedProviders("google, azure_b2c"));
    }

    [Fact]
    public void Expand_Deduplicates_CaseInsensitively()
    {
        var index = BuildIndex(ThreeProviders());
        Assert.Equal(new[] { "google" }, index.ExpandAllowedProviders("google,GOOGLE,google"));
    }

    [Fact]
    public void Expand_Wildcard_ReturnsAllConfiguredProviders()
    {
        var index = BuildIndex(ThreeProviders());
        Assert.Equal(
            new HashSet<string> { "google", "azure_b2c", "auth0" },
            index.ExpandAllowedProviders("*").ToHashSet());
    }

    [Fact]
    public void Expand_WildcardMixedWithNames_StillReturnsAll()
    {
        var index = BuildIndex(ThreeProviders());
        Assert.Equal(
            new HashSet<string> { "google", "azure_b2c", "auth0" },
            index.ExpandAllowedProviders("google,*").ToHashSet());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Expand_EmptyOrWhitespace_ReturnsEmpty(string? raw)
    {
        var index = BuildIndex(ThreeProviders());
        Assert.Empty(index.ExpandAllowedProviders(raw));
    }

    // ── ResolveProviderByIssuer ──────────────────────────────────────────────

    [Fact]
    public void Resolve_ByAuthorityAsIssuer_WhenNoExplicitIssuer()
    {
        var index = BuildIndex(ThreeProviders());
        var resolved = index.ResolveProviderByIssuer(
            "https://accounts.google.com", new[] { "google-client-id" }, new[] { "google", "azure_b2c" });
        Assert.Equal("google", resolved);
    }

    [Fact]
    public void Resolve_ByExplicitIssuer_WhenDifferentFromAuthority()
    {
        var index = BuildIndex(ThreeProviders());
        var resolved = index.ResolveProviderByIssuer(
            "https://b2c.example.com/guid/v2.0/", new[] { "b2c-app-id" }, new[] { "google", "azure_b2c" });
        Assert.Equal("azure_b2c", resolved);
    }

    [Fact]
    public void Resolve_IgnoresProvidersNotInAllowedList()
    {
        var index = BuildIndex(ThreeProviders());
        // Token is from Google, but the endpoint only allows azure_b2c -> no match.
        var resolved = index.ResolveProviderByIssuer(
            "https://accounts.google.com", new[] { "google-client-id" }, new[] { "azure_b2c" });
        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_UnknownIssuer_ReturnsNull()
    {
        var index = BuildIndex(ThreeProviders());
        var resolved = index.ResolveProviderByIssuer(
            "https://evil.example.com", new[] { "x" }, new[] { "google", "azure_b2c" });
        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_IssuerMustMatchExactly_NoTrailingSlashTolerance()
    {
        var index = BuildIndex(ThreeProviders());
        // Off by a trailing slash from the configured authority -> intentionally no match,
        // because token validation also compares the issuer exactly (routing must agree with it).
        var resolved = index.ResolveProviderByIssuer(
            "https://accounts.google.com/", new[] { "google-client-id" }, new[] { "google" });
        Assert.Null(resolved);
    }

    // ── Shared issuer (two app registrations in one tenant) ───────────────────

    private static Dictionary<string, string?> SharedIssuer() => new()
    {
        ["authorize:providers:app1:authority"] = "https://login.microsoftonline.com/tenant/v2.0",
        ["authorize:providers:app1:issuer"] = "https://login.microsoftonline.com/tenant/v2.0",
        ["authorize:providers:app1:audience"] = "aud-app1",

        ["authorize:providers:app2:authority"] = "https://login.microsoftonline.com/tenant/v2.0",
        ["authorize:providers:app2:issuer"] = "https://login.microsoftonline.com/tenant/v2.0",
        ["authorize:providers:app2:audience"] = "aud-app2",
    };

    [Fact]
    public void Resolve_SharedIssuer_DisambiguatesByAudience()
    {
        var index = BuildIndex(SharedIssuer());
        var resolved = index.ResolveProviderByIssuer(
            "https://login.microsoftonline.com/tenant/v2.0", new[] { "aud-app2" }, new[] { "app1", "app2" });
        Assert.Equal("app2", resolved);
    }

    [Fact]
    public void Resolve_SharedIssuer_NoMatchingAudience_ReturnsNull()
    {
        var index = BuildIndex(SharedIssuer());
        var resolved = index.ResolveProviderByIssuer(
            "https://login.microsoftonline.com/tenant/v2.0", new[] { "aud-unknown" }, new[] { "app1", "app2" });
        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_SharedIssuer_NoAudienceProvided_IsAmbiguous_ReturnsNull()
    {
        var index = BuildIndex(SharedIssuer());
        var resolved = index.ResolveProviderByIssuer(
            "https://login.microsoftonline.com/tenant/v2.0", tokenAudiences: null, new[] { "app1", "app2" });
        Assert.Null(resolved);
    }

    [Fact]
    public void ProviderNames_ReflectsConfiguration()
    {
        var index = BuildIndex(ThreeProviders());
        Assert.Equal(
            new HashSet<string> { "google", "azure_b2c", "auth0" },
            index.ProviderNames.ToHashSet());
    }
}
