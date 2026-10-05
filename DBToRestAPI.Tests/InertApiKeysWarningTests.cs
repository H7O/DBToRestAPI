using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// Pins the warning for a route that looks protected by API keys but names no collection in
/// <c>&lt;api_keys_collections&gt;</c>: nothing reads a route's <c>api_keys</c>, so such a route answers
/// callers without a key while reading as protected.
/// </summary>
public class InertApiKeysWarningTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IConfigurationRoot Config(Dictionary<string, string?> data)
        => new ConfigurationBuilder().AddInMemoryCollection(data).Build();

    [Fact]
    public void QueryRouteWithApiKeysAndNoCollection_IsReportedOnceAtLoad()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:open_ep:route"] = "open",
            ["queries:open_ep:api_keys:key"] = "k1",
            ["queries:guarded_ep:route"] = "guarded",
            ["queries:guarded_ep:api_keys_collections"] = "internal",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("`queries:open_ep`", entry.Message);
        Assert.Contains("<api_keys_collections>", entry.Message);
    }

    [Fact]
    public void GatewayRouteWithApiKeysAndNoCollection_IsReported()
    {
        var log = new ListLogger<RouteConfigResolver>();
        var config = Config(new()
        {
            ["routes:open_gw:url"] = "https://example.com/",
            ["routes:open_gw:api_keys:key"] = "k1",
        });

        _ = new RouteConfigResolver(new TestEncryptedConfiguration(config), log);

        Assert.Contains("`routes:open_gw`", Assert.Single(log.Entries).Message);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" , ")]
    public void ApiKeysWithAnEmptyCollectionList_IsReported(string collections)
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:api_keys:key"] = "k1",
            ["queries:ep:api_keys_collections"] = collections,
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        Assert.Single(log.Entries);
    }

    [Fact]
    public void WildcardGatewayRoute_IsReportedWithItsRoute()
    {
        var log = new ListLogger<RouteConfigResolver>();
        var config = Config(new()
        {
            ["routes:gw:route"] = "api/vendor/*",
            ["routes:gw:url"] = "https://example.com/",
            ["routes:gw:api_keys:key"] = "k1",
        });

        _ = new RouteConfigResolver(new TestEncryptedConfiguration(config), log);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("`api/vendor/*`", message);
        Assert.Contains("`routes:gw`", message);
    }

    [Fact]
    public void DuplicateSiblingSections_AreCheckedOneByOne()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:dup:0:route"] = "first",
            ["queries:dup:0:query"] = "SELECT 1",
            ["queries:dup:0:api_keys:key"] = "k1",
            ["queries:dup:1:route"] = "second",
            ["queries:dup:1:query"] = "SELECT 2",
            ["queries:dup:1:api_keys_collections"] = "internal",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("`first`", message);
        Assert.Contains("`queries:dup:0`", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public void CollectionsWithNoNameAndNoApiKeys_IsReported(string collections)
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:api_keys_collections"] = collections,
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        Assert.Contains("names no collection", Assert.Single(log.Entries).Message);
    }

    [Fact]
    public void CollectionsWithKeysInsteadOfNames_IsReported()
    {
        // The layout of api_keys.xml, copied into a route: it reads back as no collection at all.
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:api_keys_collections:vendors:key"] = "k1",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        Assert.Contains("names no collection", Assert.Single(log.Entries).Message);
    }

    [Fact]
    public void RoutesWithoutApiKeys_AreNotReported()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:plain:route"] = "plain",
            ["queries:guarded:route"] = "guarded",
            ["queries:guarded:api_keys_collections"] = "internal",
            ["queries:both:route"] = "both",
            ["queries:both:api_keys:key"] = "k1",
            ["queries:both:api_keys_collections"] = "internal",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Reload_RepeatsTheWarningOnlyWhenTheMistakeComesBack()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:api_keys:key"] = "k1",
        });
        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);
        Assert.Single(log.Entries);

        config.Reload();                                   // same configuration loaded again
        Assert.Single(log.Entries);

        config["queries:ep:api_keys_collections"] = "internal";
        config.Reload();                                   // fixed
        Assert.Single(log.Entries);

        config["queries:ep:api_keys_collections"] = "";
        config.Reload();                                   // broken again
        Assert.Equal(2, log.Entries.Count);
    }
}
