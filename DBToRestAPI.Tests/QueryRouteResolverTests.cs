using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace DBToRestAPI.Tests;

/// <summary>
/// Minimal IEncryptedConfiguration wrapper over IConfigurationRoot for testing purposes.
/// </summary>
internal sealed class TestEncryptedConfiguration : IEncryptedConfiguration
{
    private readonly IConfigurationRoot _inner;
    public TestEncryptedConfiguration(IConfigurationRoot inner) => _inner = inner;

    public string? this[string key]
    {
        get => _inner[key];
        set => _inner[key] = value;
    }
    public string? GetConnectionString(string name) => null;
    public bool HasDecryptedValue(string key) => false;
    public IReadOnlyCollection<string> GetDecryptedPaths() => [];
    public bool IsActive => false;
    public IEnumerable<IConfigurationSection> GetChildren() => _inner.GetChildren();
    public IConfigurationSection GetSection(string key) => _inner.GetSection(key);
    public IChangeToken GetReloadToken() => _inner.GetReloadToken();
}

public class QueryRouteResolverTests
{
    /// <summary>
    /// Builds a QueryRouteResolver from in-memory key/value pairs that simulate
    /// the IConfiguration tree produced by XmlConfigurationProvider.
    /// </summary>
    private static QueryRouteResolver BuildResolver(Dictionary<string, string?> data)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();
        return new QueryRouteResolver(new TestEncryptedConfiguration(config));
    }

    // ──────────────────────────────────────────────
    //  Basic host matching — exact host
    // ──────────────────────────────────────────────

    [Fact]
    public void ExactHost_MatchesCorrectHost()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "users",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "www.example1.com",
            ["queries:ep1:query"] = "SELECT 1",

            ["queries:ep2:route"] = "users",
            ["queries:ep2:verb"] = "GET",
            ["queries:ep2:host"] = "www.example2.com",
            ["queries:ep2:query"] = "SELECT 2",
        });

        var result1 = resolver.ResolveRoute("users", "GET", "www.example1.com");
        var result2 = resolver.ResolveRoute("users", "GET", "www.example2.com");

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal("SELECT 1", result1.GetValue<string>("query"));
        Assert.Equal("SELECT 2", result2.GetValue<string>("query"));
    }

    [Fact]
    public void ExactHost_NoMatchForDifferentHost()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "users",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "www.example1.com",
            ["queries:ep1:query"] = "SELECT 1",
        });

        var result = resolver.ResolveRoute("users", "GET", "www.other.com");
        Assert.Null(result);
    }

    [Fact]
    public void ExactHost_CaseInsensitive()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "users",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "WWW.EXAMPLE.COM",
            ["queries:ep1:query"] = "SELECT 1",
        });

        var result = resolver.ResolveRoute("users", "GET", "www.example.com");
        Assert.NotNull(result);
        Assert.Equal("SELECT 1", result.GetValue<string>("query"));
    }

    // ──────────────────────────────────────────────
    //  Wildcard host matching
    // ──────────────────────────────────────────────

    [Fact]
    public void WildcardHost_MatchesSubdomain()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "data",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "*.example.com",
            ["queries:ep1:query"] = "SELECT wildcard",
        });

        var result = resolver.ResolveRoute("data", "GET", "api.example.com");
        Assert.NotNull(result);
        Assert.Equal("SELECT wildcard", result.GetValue<string>("query"));
    }

    [Fact]
    public void WildcardHost_MatchesDeepSubdomain()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "data",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "*.example.com",
            ["queries:ep1:query"] = "SELECT wildcard",
        });

        var result = resolver.ResolveRoute("data", "GET", "deep.sub.example.com");
        Assert.NotNull(result);
    }

    [Fact]
    public void WildcardHost_MultiLevel_MatchesSubdomain()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "data",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "*.api.example.com",
            ["queries:ep1:query"] = "SELECT multilevel",
        });

        var result = resolver.ResolveRoute("data", "GET", "v1.api.example.com");
        Assert.NotNull(result);
        Assert.Equal("SELECT multilevel", result.GetValue<string>("query"));
    }

    [Fact]
    public void WildcardHost_DoesNotMatchBareBaseDomain()
    {
        // *.example.com should NOT match "example.com" itself —
        // there must be at least one character before the suffix.
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "data",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "*.example.com",
            ["queries:ep1:query"] = "SELECT wildcard",
        });

        var result = resolver.ResolveRoute("data", "GET", "example.com");
        Assert.Null(result);
    }

    [Fact]
    public void WildcardHost_DoesNotMatchDotOnly()
    {
        // ".example.com" is not a valid subdomain — need at least one char before the dot.
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "data",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "*.example.com",
            ["queries:ep1:query"] = "SELECT wildcard",
        });

        var result = resolver.ResolveRoute("data", "GET", ".example.com");
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────
    //  No host constraint — backward compatibility
    // ──────────────────────────────────────────────

    [Fact]
    public void NoHostConstraint_MatchesAnyHost()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "open",
            ["queries:ep1:verb"] = "GET",
            // No "host" key → null → matches everything
            ["queries:ep1:query"] = "SELECT open",
        });

        Assert.NotNull(resolver.ResolveRoute("open", "GET", "anything.com"));
        Assert.NotNull(resolver.ResolveRoute("open", "GET", "localhost"));
        Assert.NotNull(resolver.ResolveRoute("open", "GET", null));
    }

    [Fact]
    public void NoHostConstraint_NullRequestHost_StillMatches()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "open",
            ["queries:ep1:query"] = "SELECT open",
        });

        var result = resolver.ResolveRoute("open", "GET", null);
        Assert.NotNull(result);
    }

    [Fact]
    public void HostConstraint_Set_NullRequestHost_NoMatch()
    {
        // If a host constraint is configured but the request has no host → no match.
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "restricted",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "www.example.com",
            ["queries:ep1:query"] = "SELECT restricted",
        });

        var result = resolver.ResolveRoute("restricted", "GET", null);
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────
    //  Specificity tie-breaking
    // ──────────────────────────────────────────────

    [Fact]
    public void Specificity_ExactHost_BeatsWildcard()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:wild:route"] = "items",
            ["queries:wild:verb"] = "GET",
            ["queries:wild:host"] = "*.example.com",
            ["queries:wild:query"] = "SELECT wildcard",

            ["queries:exact:route"] = "items",
            ["queries:exact:verb"] = "GET",
            ["queries:exact:host"] = "api.example.com",
            ["queries:exact:query"] = "SELECT exact",
        });

        var result = resolver.ResolveRoute("items", "GET", "api.example.com");
        Assert.NotNull(result);
        Assert.Equal("SELECT exact", result.GetValue<string>("query"));
    }

    [Fact]
    public void Specificity_ExactHost_BeatsNoConstraint()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:open:route"] = "items",
            ["queries:open:verb"] = "GET",
            // No host → matches all
            ["queries:open:query"] = "SELECT open",

            ["queries:specific:route"] = "items",
            ["queries:specific:verb"] = "GET",
            ["queries:specific:host"] = "www.example.com",
            ["queries:specific:query"] = "SELECT specific",
        });

        var result = resolver.ResolveRoute("items", "GET", "www.example.com");
        Assert.NotNull(result);
        Assert.Equal("SELECT specific", result.GetValue<string>("query"));
    }

    [Fact]
    public void Specificity_WildcardHost_BeatsNoConstraint()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:open:route"] = "items",
            ["queries:open:verb"] = "GET",
            ["queries:open:query"] = "SELECT open",

            ["queries:wild:route"] = "items",
            ["queries:wild:verb"] = "GET",
            ["queries:wild:host"] = "*.example.com",
            ["queries:wild:query"] = "SELECT wildcard",
        });

        var result = resolver.ResolveRoute("items", "GET", "api.example.com");
        Assert.NotNull(result);
        Assert.Equal("SELECT wildcard", result.GetValue<string>("query"));
    }

    [Fact]
    public void Specificity_FallbackToNoConstraint_WhenHostDoesNotMatch()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:open:route"] = "items",
            ["queries:open:verb"] = "GET",
            ["queries:open:query"] = "SELECT open",

            ["queries:specific:route"] = "items",
            ["queries:specific:verb"] = "GET",
            ["queries:specific:host"] = "www.example.com",
            ["queries:specific:query"] = "SELECT specific",
        });

        // Request comes from a host that does NOT match the specific one,
        // so should fall back to the unconstrained route.
        var result = resolver.ResolveRoute("items", "GET", "www.other.com");
        Assert.NotNull(result);
        Assert.Equal("SELECT open", result.GetValue<string>("query"));
    }

    // ──────────────────────────────────────────────
    //  Host matching with parameterized routes
    // ──────────────────────────────────────────────

    [Fact]
    public void ParameterizedRoute_HostFiltering()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "users/{{id}}",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "www.site1.com",
            ["queries:ep1:query"] = "SELECT site1",

            ["queries:ep2:route"] = "users/{{id}}",
            ["queries:ep2:verb"] = "GET",
            ["queries:ep2:host"] = "www.site2.com",
            ["queries:ep2:query"] = "SELECT site2",
        });

        var r1 = resolver.ResolveRoute("users/123", "GET", "www.site1.com");
        var r2 = resolver.ResolveRoute("users/123", "GET", "www.site2.com");

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal("SELECT site1", r1.GetValue<string>("query"));
        Assert.Equal("SELECT site2", r2.GetValue<string>("query"));
    }

    [Fact]
    public void ParameterizedRoute_HostSpecificity_ExactBeatsWildcard()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:wild:route"] = "orders/{{id}}",
            ["queries:wild:verb"] = "GET",
            ["queries:wild:host"] = "*.example.com",
            ["queries:wild:query"] = "SELECT wildcard",

            ["queries:exact:route"] = "orders/{{id}}",
            ["queries:exact:verb"] = "GET",
            ["queries:exact:host"] = "api.example.com",
            ["queries:exact:query"] = "SELECT exact",
        });

        var result = resolver.ResolveRoute("orders/42", "GET", "api.example.com");
        Assert.NotNull(result);
        Assert.Equal("SELECT exact", result.GetValue<string>("query"));
    }

    // ──────────────────────────────────────────────
    //  ResolveRoutes (OPTIONS) with host filtering
    // ──────────────────────────────────────────────

    [Fact]
    public void ResolveRoutes_ReturnsOnlyHostMatchingEndpoints()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "products",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "www.site1.com",
            ["queries:ep1:query"] = "SELECT 1",

            ["queries:ep2:route"] = "products",
            ["queries:ep2:verb"] = "POST",
            ["queries:ep2:host"] = "www.site2.com",
            ["queries:ep2:query"] = "SELECT 2",

            ["queries:ep3:route"] = "products",
            ["queries:ep3:verb"] = "DELETE",
            // no host constraint
            ["queries:ep3:query"] = "SELECT 3",
        });

        // site1 should see GET (host match) + DELETE (no constraint)
        var site1Results = resolver.ResolveRoutes("products", "www.site1.com");
        Assert.Equal(2, site1Results.Count);

        // site2 should see POST (host match) + DELETE (no constraint)
        var site2Results = resolver.ResolveRoutes("products", "www.site2.com");
        Assert.Equal(2, site2Results.Count);

        // unknown host should see only DELETE (no constraint)
        var unknownResults = resolver.ResolveRoutes("products", "www.unknown.com");
        Assert.Single(unknownResults);
    }

    // ──────────────────────────────────────────────
    //  Port stripping is caller's responsibility
    //  (verifying host-only matching works correctly)
    // ──────────────────────────────────────────────

    [Fact]
    public void PortNotIncluded_MatchesCorrectly()
    {
        // The middleware passes Host.Host (port-free), so the resolver
        // should never see a port. This test just confirms that passing
        // a host without port works fine with exact match.
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:ep1:route"] = "health",
            ["queries:ep1:verb"] = "GET",
            ["queries:ep1:host"] = "www.example.com",
            ["queries:ep1:query"] = "SELECT 1",
        });

        var result = resolver.ResolveRoute("health", "GET", "www.example.com");
        Assert.NotNull(result);
    }

    // ──────────────────────────────────────────────
    //  Mixed: different verbs same route same host
    // ──────────────────────────────────────────────

    [Fact]
    public void SameRouteSameHost_DifferentVerbs_ResolvesCorrectly()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:get_users:route"] = "users",
            ["queries:get_users:verb"] = "GET",
            ["queries:get_users:host"] = "api.example.com",
            ["queries:get_users:query"] = "SELECT users",

            ["queries:create_user:route"] = "users",
            ["queries:create_user:verb"] = "POST",
            ["queries:create_user:host"] = "api.example.com",
            ["queries:create_user:query"] = "INSERT user",
        });

        var getResult = resolver.ResolveRoute("users", "GET", "api.example.com");
        var postResult = resolver.ResolveRoute("users", "POST", "api.example.com");

        Assert.NotNull(getResult);
        Assert.NotNull(postResult);
        Assert.Equal("SELECT users", getResult.GetValue<string>("query"));
        Assert.Equal("INSERT user", postResult.GetValue<string>("query"));
    }

    // ──────────────────────────────────────────────
    //  Edge case: same route, same verb, no host — still works (first match)
    // ──────────────────────────────────────────────

    [Fact]
    public void NoHost_StillWorksAsBeforeForLegacyEndpoints()
    {
        var resolver = BuildResolver(new Dictionary<string, string?>
        {
            ["queries:hello:route"] = "hello",
            ["queries:hello:query"] = "SELECT 'hello'",
        });

        // No verb restriction, no host → should match any verb, any host
        var result = resolver.ResolveRoute("hello", "GET", "localhost");
        Assert.NotNull(result);
        Assert.Equal("SELECT 'hello'", result.GetValue<string>("query"));
    }
}
