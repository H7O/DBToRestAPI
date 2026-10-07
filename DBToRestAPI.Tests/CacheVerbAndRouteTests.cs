using System.Text.Json;
using Com.H.Data.Common;
using DBToRestAPI.Cache;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// Which requests may use a cached endpoint's stored response, and what tells two entries apart.
///
/// Before 1.7.6 the key held only the endpoint's element name and the &lt;invalidators&gt; values.
/// An endpoint with no &lt;verb&gt; answers every verb, so a POST, PUT or DELETE to a cached
/// endpoint got the cached GET response and its SQL never ran. And a route value the author
/// didn't list in &lt;invalidators&gt; shared one entry across every id.
/// </summary>
public class CacheVerbAndRouteTests
{
    private const string MarkerRegex = @"(?<open_marker>\{\{)(?<param>.*?)?(?<close_marker>\}\})";
    private const string HeaderRegex = @"(?<open_marker>\{\{|\{h\{)(?<param>.*?)?(?<close_marker>\}\})";
    private const string QueryStringRegex = @"(?<open_marker>\{\{|\{qs\{)(?<param>.*?)?(?<close_marker>\}\})";

    private static (CacheService Cache, IConfigurationRoot Config) Create()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["queries:cached:route"] = "items/{{id}}",
                ["queries:cached:cache:memory:duration_in_milliseconds"] = "60000",
                ["queries:other:route"] = "other/{{id}}",
                ["queries:other:cache:memory:duration_in_milliseconds"] = "60000",
                ["queries:with_invalidator:cache:memory:duration_in_milliseconds"] = "60000",
                ["queries:with_invalidator:cache:memory:invalidators"] = "category",
                ["routes:gateway:cache:memory:duration_in_milliseconds"] = "60000",
                ["routes:gateway_tags:cache:memory:duration_in_milliseconds"] = "60000",
                ["routes:gateway_tags:cache:memory:invalidators"] = "tag,category",
                ["queries:tenant_scoped:cache:memory:duration_in_milliseconds"] = "60000",
                ["queries:tenant_scoped:cache:memory:invalidators"] = "tenant",
                // From 1.7.8 a name may contain a space, when the route's query uses it.
                ["queries:sorted:cache:memory:duration_in_milliseconds"] = "60000",
                ["queries:sorted:cache:memory:invalidators"] = "sort by",
                ["queries:sorted:query"] = "SELECT * FROM items ORDER BY CASE WHEN {{sort by}} = 'name' THEN name END",
                // Before 1.7.8 a space separated names; the query uses the parts, so it still does.
                ["queries:legacy:cache:memory:duration_in_milliseconds"] = "60000",
                ["queries:legacy:cache:memory:invalidators"] = "tenant_id user_id",
                ["queries:legacy:query"] = "SELECT * FROM items WHERE tenant_id = {{tenant_id}} AND owner = {{user_id}}",
                // A gateway runs no query: spaces and `;` separate its names, as before 1.7.8.
                ["routes:gateway_split:cache:memory:duration_in_milliseconds"] = "60000",
                ["routes:gateway_split:cache:memory:invalidators"] = "tag category;region",
                // Two sibling groups whose endpoints share the element name "0": the old key used
                // only that last segment.
                ["queries:group_a:0:cache:memory:duration_in_milliseconds"] = "60000",
                ["queries:group_b:0:cache:memory:duration_in_milliseconds"] = "60000",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        return (new CacheService(new TestEncryptedConfiguration(config), sp, sp.GetRequiredService<HybridCache>()), config);
    }

    /// <summary>
    /// A request as Step1 leaves it: the raw requested route, and the route values it resolved.
    /// </summary>
    private static HttpContext Request(string method, string route, string? id = null, string? query = null,
        params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Items["route"] = route;
        if (id != null)
            context.Items["route_parameters"] = new Dictionary<string, string> { ["id"] = id };
        if (query != null)
            context.Request.QueryString = new QueryString(query);
        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;
        return context;
    }

    private static List<DbQueryParams> Params(object? values = null) =>
        [new() { DataModel = values ?? new Dictionary<string, object>(), QueryParamsRegex = MarkerRegex }];

    /// <summary>
    /// Calls the endpoint through the cache and returns the factory's call number that produced
    /// the response, so a cached answer shows the number of an earlier call.
    /// </summary>
    private sealed class Endpoint
    {
        public int Calls;
        public readonly List<bool> Materialised = [];

        public async Task<int> CallAsync(CacheService cache, IConfigurationSection section, HttpContext context, object? values = null)
        {
            var result = await cache.GetQueryResultAsActionAsync(section, context, Params(values), materialise =>
            {
                Materialised.Add(materialise);
                return Task.FromResult<IActionResult>(new ObjectResult(new { call = ++Calls }) { StatusCode = 200 });
            });
            var value = ((ObjectResult)result).Value!;
            return value is JsonElement e ? e.GetProperty("call").GetInt32() : (int)value.GetType().GetProperty("call")!.GetValue(value)!;
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task WriteVerb_AfterACachedGet_RunsItsSql(string verb)
    {
        var (cache, config) = Create();
        var section = config.GetSection("queries:cached");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/1", "1")));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request(verb, "items/1", "1")));
        Assert.Equal(3, await endpoint.CallAsync(cache, section, Request(verb, "items/1", "1")));

        // The writes streamed (not materialised for the cache), and the GET entry is untouched.
        Assert.Equal(new[] { true, false, false }, endpoint.Materialised);
        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/1", "1")));
    }

    [Fact]
    public async Task Get_SameRoute_IsServedFromTheCache()
    {
        var (cache, config) = Create();
        var section = config.GetSection("queries:cached");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/1", "1")));
        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/1", "1")));
        Assert.Equal(1, endpoint.Calls);
    }

    [Fact]
    public async Task Get_DifferentRouteValues_GetTheirOwnEntries()
    {
        // No <invalidators> at all: the route still tells the two ids apart.
        var (cache, config) = Create();
        var section = config.GetSection("queries:cached");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/1", "1")));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("GET", "items/2", "2")));
        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/1", "1")));
    }

    [Fact]
    public async Task Get_SpellingsOfOneRoute_ShareOneEntry()
    {
        // Routing ignores letter case in literal segments and extra slashes, so all of these run
        // the same SQL with id=1. Keyed on the raw path, each stored its own copy of the answer.
        var (cache, config) = Create();
        var section = config.GetSection("queries:cached");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/1", "1")));
        foreach (var spelling in new[] { "Items/1", "items//1", "items/1/", "ITEMS////1" })
            Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", spelling, "1")));
        Assert.Equal(1, endpoint.Calls);
    }

    [Fact]
    public async Task Get_RouteValuesDifferingOnlyInCase_GetTheirOwnEntries()
    {
        // A route value is passed to the SQL as sent, so its case can change the answer.
        var (cache, config) = Create();
        var section = config.GetSection("queries:cached");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/abc", "abc")));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("GET", "items/ABC", "ABC")));
    }

    [Fact]
    public async Task Get_SiblingEndpointsWithTheSameElementName_DoNotShareAnEntry()
    {
        var (cache, config) = Create();
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:group_a:0"), Request("GET", "x")));
        Assert.Equal(2, await endpoint.CallAsync(cache, config.GetSection("queries:group_b:0"), Request("GET", "x")));
    }

    [Fact]
    public async Task Get_TwoEndpoints_SameRouteText_DoNotShareAnEntry()
    {
        var (cache, config) = Create();
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:cached"), Request("GET", "x")));
        Assert.Equal(2, await endpoint.CallAsync(cache, config.GetSection("queries:other"), Request("GET", "x")));
    }

    [Fact]
    public async Task Head_DoesNotReuseTheGetEntry()
    {
        var (cache, config) = Create();
        var section = config.GetSection("queries:cached");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "items/1", "1")));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("HEAD", "items/1", "1")));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("HEAD", "items/1", "1")));
    }

    [Fact]
    public async Task Invalidator_NameWithASpace_SeparatesEntries()
    {
        var (cache, config) = Create();
        var section = config.GetSection("queries:sorted");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), new Dictionary<string, object> { ["sort by"] = "price" }));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), new Dictionary<string, object> { ["sort by"] = "name" }));
        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), new Dictionary<string, object> { ["sort by"] = "price" }));
    }

    [Fact]
    public async Task Invalidators_AreReadAgainWhenTheConfigurationReloads()
    {
        // The engine keeps each route's invalidator names until the configuration reloads.
        var (cache, config) = Create();
        var section = config.GetSection("queries:sorted");
        var endpoint = new Endpoint();
        Dictionary<string, object> Sort(string value) => new() { ["sort by"] = value };

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), Sort("a")));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), Sort("b")));

        // The query no longer uses {{sort by}}, so the name is split into `sort` and `by`, which
        // match no input: every value now shares one entry.
        config["queries:sorted:query"] = "SELECT * FROM items";
        config.Reload();

        Assert.Equal(3, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), Sort("c")));
        Assert.Equal(3, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), Sort("d")));
    }

    [Fact]
    public async Task Invalidators_FollowTheConfiguration_WithoutWaitingForAReloadSignal()
    {
        // A remembered answer is used only while the query texts it came from are unchanged, so
        // it can't outlive a change, whatever order reload callbacks run in. Setting a value here
        // changes the configuration without raising a reload.
        var (cache, config) = Create();
        var section = config.GetSection("queries:sorted");
        var endpoint = new Endpoint();
        Dictionary<string, object> Sort(string value) => new() { ["sort by"] = value };

        config["queries:sorted:query"] = "SELECT * FROM items";
        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), Sort("a")));
        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), Sort("b")));

        config["queries:sorted:query"] = "SELECT * FROM items ORDER BY {{sort by}}";
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), Sort("a")));
        Assert.Equal(3, await endpoint.CallAsync(cache, section, Request("GET", "sorted"), Sort("b")));
    }

    [Fact]
    public async Task Invalidators_SeparatedBySpaces_AsBefore178_StillKeyEachInput()
    {
        // Read as one name, `tenant_id user_id` would match nothing, and one tenant's cached
        // answer would be served to the other.
        var (cache, config) = Create();
        var section = config.GetSection("queries:legacy");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "legacy"), new Dictionary<string, object> { ["tenant_id"] = "a", ["user_id"] = "x" }));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("GET", "legacy"), new Dictionary<string, object> { ["tenant_id"] = "b", ["user_id"] = "x" }));
        Assert.Equal(3, await endpoint.CallAsync(cache, section, Request("GET", "legacy"), new Dictionary<string, object> { ["tenant_id"] = "a", ["user_id"] = "y" }));
    }

    [Theory]
    [InlineData("a,b", "SELECT 1", new[] { "a", "b" })]
    [InlineData("sort by", "SELECT {{sort by}}", new[] { "sort by" })]
    [InlineData("sort by", "SELECT {qs{Sort By}}", new[] { "sort by" })]       // any marker form, any case
    [InlineData("tenant_id user_id", "SELECT {{tenant_id}}", new[] { "tenant_id", "user_id" })]
    [InlineData("a;b", "SELECT {{a}}", new[] { "a", "b" })]
    [InlineData("last, first|e-mail", "SELECT {{last, first}}", new[] { "last, first", "e-mail" })]
    // A name the queries don't use is split as before 1.7.8, so no input drops out of the key.
    [InlineData("last, first|e-mail", "SELECT 1", new[] { "last", "first", "e-mail" })]
    [InlineData("tenant_id,user_id|region", "SELECT {{tenant_id}}", new[] { "tenant_id", "user_id", "region" })]
    // Only a marker counts: the name in a comment doesn't keep it whole.
    [InlineData("tenant_id user_id", "-- per tenant_id user_id\nSELECT {{tenant_id}}, {{user_id}}", new[] { "tenant_id", "user_id" })]
    public void GetInvalidators_SplitsNames(string list, string query, string[] expected)
    {
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["queries:ep:query"] = query })
            .Build()
            .GetSection("queries:ep");

        Assert.Equal(expected, CacheService.GetInvalidators(section, list));
    }

    [Fact]
    public void GetInvalidators_ReadsEveryQueryOfAChain()
    {
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["queries:ep:query:0"] = "SELECT 1 AS x",
                ["queries:ep:query:1"] = "SELECT {{sort by}}",
            })
            .Build()
            .GetSection("queries:ep");

        Assert.Equal(new[] { "sort by" }, CacheService.GetInvalidators(section, "sort by"));
    }

    private const string PipeMarker = @"(?<open_marker>\|\|)(?<param>.*?)?(?<close_marker>\|\|)";

    [Fact]
    public void GetInvalidators_ReadsTheRoutesOwnMarkerDelimiters()
    {
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["queries:ep:query_string_variables_pattern"] = PipeMarker,
                ["queries:ep:query"] = "SELECT * FROM items ORDER BY CASE WHEN ||sort by|| = 'name' THEN name END",
            })
            .Build()
            .GetSection("queries:ep");

        Assert.Equal(new[] { "sort by" }, CacheService.GetInvalidators(section, "sort by"));
    }

    [Fact]
    public void GetInvalidators_ReadsTheGlobalMarkerDelimiters()
    {
        var root = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["regex:json_variables_pattern"] = PipeMarker,
                ["queries:ep:query"] = "SELECT ||sort by||",
            })
            .Build();

        Assert.Equal(new[] { "sort by" }, CacheService.GetInvalidators(root.GetSection("queries:ep"), "sort by", root));
        Assert.Equal(new[] { "sort", "by" }, CacheService.GetInvalidators(root.GetSection("queries:ep"), "sort by"));
    }

    [Fact]
    public void GetInvalidators_ReadsTheCurrentQuery_ThroughTheEnginesConfiguration()
    {
        // The engine's configuration wrappers answer an indexer read from the snapshot the section
        // was built from, and GetSection from the current configuration, which is what a request
        // runs. A section the route resolver captured before a reload must be judged against the
        // new SQL. A key folder turns encryption on everywhere, so the service rebuilds on reload.
        var keys = Path.Combine(Path.GetTempPath(), "dbtorest-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["settings_encryption:data_protection_key_path"] = keys,
                    ["queries:ep:query"] = "SELECT 1",
                })
                .Build();
            var engineConfiguration = new SettingsEncryptionService(root, NullLogger<SettingsEncryptionService>.Instance);
            var section = engineConfiguration.GetSection("queries:ep");

            root["queries:ep:query"] = "SELECT * FROM t WHERE region = {{sales region}}";
            root.Reload();

            Assert.Equal(new[] { "sales region" }, CacheService.GetInvalidators(section, "sales region", engineConfiguration));
        }
        finally
        {
            try { Directory.Delete(keys, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void GetInvalidators_ReadsTheCurrentMarkerDelimiters_ThroughTheEnginesConfiguration()
    {
        // As above, for a route that switches to its own delimiters in the same save.
        var keys = Path.Combine(Path.GetTempPath(), "dbtorest-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["settings_encryption:data_protection_key_path"] = keys,
                    ["queries:ep:query"] = "SELECT 1",
                })
                .Build();
            var engineConfiguration = new SettingsEncryptionService(root, NullLogger<SettingsEncryptionService>.Instance);
            var section = engineConfiguration.GetSection("queries:ep");

            root["queries:ep:query_string_variables_pattern"] = PipeMarker;
            root["queries:ep:query"] = "SELECT * FROM t WHERE region = ||sales region||";
            root.Reload();

            Assert.Equal(new[] { "sales region" }, CacheService.GetInvalidators(section, "sales region", engineConfiguration));
        }
        finally
        {
            try { Directory.Delete(keys, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("query:0", "query:2")]     // an empty <query/> in a chain leaves a gap in the numbering
    [InlineData("query:0", "query:main")]  // a name attribute names the child
    public void GetInvalidators_ReadsEveryQueryTheParserRuns(string firstKey, string secondKey)
    {
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"queries:ep:{firstKey}"] = "SELECT 1 AS x",
                [$"queries:ep:{secondKey}"] = "SELECT {{x y}}",
            })
            .Build()
            .GetSection("queries:ep");

        Assert.Equal(new[] { "x y" }, CacheService.GetInvalidators(section, "x y"));
    }

    [Fact]
    public void GetInvalidators_ReadsTheCountQuery()
    {
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["queries:ep:query"] = "SELECT 1 AS x",
                ["queries:ep:count_query"] = "SELECT COUNT(*) FROM items WHERE kind = {{sort by}}",
            })
            .Build()
            .GetSection("queries:ep");

        Assert.Equal(new[] { "sort by" }, CacheService.GetInvalidators(section, "sort by"));
    }

    [Fact]
    public async Task Gateway_SpacesAndSemicolons_StillSeparateNames()
    {
        var (cache, config) = Create();
        var section = config.GetSection("routes:gateway_split");
        var forwarded = 0;

        Task<string?> Forward(bool materialise) => Task.FromResult<string?>($"response {++forwarded}");

        Assert.Equal("response 1", await cache.GetForGateway(section, Request("GET", "gw/s", query: "?tag=a"), "gw/s", Forward));
        Assert.Equal("response 2", await cache.GetForGateway(section, Request("GET", "gw/s", query: "?tag=b"), "gw/s", Forward));
        Assert.Equal("response 3", await cache.GetForGateway(section, Request("GET", "gw/s", query: "?tag=a&region=x"), "gw/s", Forward));
        Assert.Equal("response 1", await cache.GetForGateway(section, Request("GET", "gw/s", query: "?tag=a"), "gw/s", Forward));
    }

    [Fact]
    public async Task Invalidators_StillSeparateEntries()
    {
        var (cache, config) = Create();
        var section = config.GetSection("queries:with_invalidator");
        var endpoint = new Endpoint();

        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "list"), new Dictionary<string, object> { ["category"] = "a" }));
        Assert.Equal(2, await endpoint.CallAsync(cache, section, Request("GET", "list"), new Dictionary<string, object> { ["category"] = "b" }));
        Assert.Equal(1, await endpoint.CallAsync(cache, section, Request("GET", "list"), new Dictionary<string, object> { ["category"] = "a" }));
    }

    [Fact]
    public async Task Gateway_Post_IsAlwaysForwarded()
    {
        // The request body is not in the gateway's key either, so a cached POST answered every
        // later POST, whatever its body, without forwarding it.
        var (cache, config) = Create();
        var section = config.GetSection("routes:gateway");
        var forwarded = 0;

        Task<string?> Forward(bool materialise) => Task.FromResult<string?>($"response {++forwarded}");

        Assert.Equal("response 1", await cache.GetForGateway(section, Request("POST", "gw/a"), "gw/a", Forward));
        Assert.Equal("response 2", await cache.GetForGateway(section, Request("POST", "gw/a"), "gw/a", Forward));
        Assert.Equal("response 3", await cache.GetForGateway(section, Request("GET", "gw/a"), "gw/a", Forward));
        Assert.Equal("response 3", await cache.GetForGateway(section, Request("GET", "gw/a"), "gw/a", Forward));
    }

    [Fact]
    public async Task Gateway_ExcludedStatus_IsNotStored_SoTheNextGetIsForwarded()
    {
        // A response whose status is in exclude_status_codes_from_cache is streamed by the
        // factory, which returns null. HybridCache stored that null, and every later GET got an
        // empty 200 without being forwarded.
        var (cache, config) = Create();
        var section = config.GetSection("routes:gateway");
        var forwarded = 0;

        Task<string?> ExcludedThenOk(bool materialise) =>
            Task.FromResult(++forwarded == 1 ? null : $"response {forwarded}");

        Assert.Null(await cache.GetForGateway(section, Request("GET", "gw/a"), "gw/a", ExcludedThenOk));
        Assert.Equal("response 2", await cache.GetForGateway(section, Request("GET", "gw/a"), "gw/a", ExcludedThenOk));
        Assert.Equal("response 2", await cache.GetForGateway(section, Request("GET", "gw/a"), "gw/a", ExcludedThenOk));
        Assert.Equal(2, forwarded);
    }

    [Fact]
    public async Task Gateway_RepeatedQueryValue_DoesNotShareTheCommaJoinedEntry()
    {
        var (cache, config) = Create();
        var section = config.GetSection("routes:gateway_tags");
        var forwarded = 0;

        Task<string?> Forward(bool materialise) => Task.FromResult<string?>($"response {++forwarded}");

        Assert.Equal("response 1", await cache.GetForGateway(section, Request("GET", "gw/a", query: "?tag=a,b"), "gw/a", Forward));
        Assert.Equal("response 2", await cache.GetForGateway(section, Request("GET", "gw/a", query: "?tag=a&tag=b"), "gw/a", Forward));
        Assert.Equal("response 1", await cache.GetForGateway(section, Request("GET", "gw/a", query: "?tag=a,b"), "gw/a", Forward));
    }

    [Fact]
    public async Task Gateway_PathCannotForgeAnInvalidatorSegment()
    {
        // The route used to be appended raw, so a path ending in `|category=<hash of v>` built
        // the same key as the path plus ?category=v, and its response was served to that caller.
        var (cache, config) = Create();
        var section = config.GetSection("routes:gateway_tags");
        var forwarded = 0;

        Task<string?> Forward(bool materialise) => Task.FromResult<string?>($"response {++forwarded}");

        var forgedRoute = "gw/a|category=" + "victim".ToXxHash3();
        Assert.Equal("response 1", await cache.GetForGateway(section, Request("GET", forgedRoute), forgedRoute, Forward));
        Assert.Equal("response 2", await cache.GetForGateway(section, Request("GET", "gw/a", query: "?category=victim"), "gw/a", Forward));
    }

    [Fact]
    public async Task Gateway_HeaderCannotStandInForAQueryStringValue()
    {
        // The upstream receives both the query string and the headers. Keyed by the name alone,
        // `?category=toys` with a `category: books` header stored the toys answer under the key
        // of `?category=books`.
        var (cache, config) = Create();
        var section = config.GetSection("routes:gateway_tags");
        var forwarded = 0;

        Task<string?> Forward(bool materialise) => Task.FromResult<string?>($"response {++forwarded}");

        Assert.Equal("response 1", await cache.GetForGateway(section,
            Request("GET", "gw/a", query: "?category=toys", headers: ("category", "books")), "gw/a", Forward));
        Assert.Equal("response 2", await cache.GetForGateway(section,
            Request("GET", "gw/a", query: "?category=books"), "gw/a", Forward));
        Assert.Equal("response 3", await cache.GetForGateway(section,
            Request("GET", "gw/a", headers: ("category", "books")), "gw/a", Forward));
        Assert.Equal("response 3", await cache.GetForGateway(section,
            Request("GET", "gw/a", headers: ("Category", "books")), "gw/a", Forward)); // header names ignore case
    }

    [Fact]
    public async Task Gateway_RepeatedQueryNamesDifferingInCase_DoNotShareAnEntry()
    {
        // Request.Query merges `Tag` and `tag`, but a case-sensitive upstream reads
        // `?Tag=a&tag=b` as tag=b and `?tag=a&tag=b` as tag=[a,b].
        var (cache, config) = Create();
        var section = config.GetSection("routes:gateway_tags");
        var forwarded = 0;

        Task<string?> Forward(bool materialise) => Task.FromResult<string?>($"response {++forwarded}");

        Assert.Equal("response 1", await cache.GetForGateway(section, Request("GET", "gw/a", query: "?Tag=a&tag=b"), "gw/a", Forward));
        Assert.Equal("response 2", await cache.GetForGateway(section, Request("GET", "gw/a", query: "?tag=a&tag=b"), "gw/a", Forward));
        Assert.Equal("response 1", await cache.GetForGateway(section, Request("GET", "gw/a", query: "?Tag=a&tag=b"), "gw/a", Forward));
    }

    [Fact]
    public async Task QueryEndpoint_SameNameFromTwoSources_IsKeyedPerSource()
    {
        // A query may read one source with its own marker ({h{tenant}}). Keyed by the name alone,
        // the last source won, so a request whose header said B and query string said A shared
        // the entry of a request whose header and query string both said A.
        var (cache, config) = Create();
        var section = config.GetSection("queries:tenant_scoped");
        var calls = 0;

        Task<IActionResult> Run(bool materialise) =>
            Task.FromResult<IActionResult>(new ObjectResult(new { call = ++calls }) { StatusCode = 200 });

        static List<DbQueryParams> Sources(string header, string query) =>
        [
            new() { DataModel = new Dictionary<string, object> { ["tenant"] = header }, QueryParamsRegex = HeaderRegex },
            new() { DataModel = new Dictionary<string, object> { ["tenant"] = query }, QueryParamsRegex = QueryStringRegex },
        ];

        await cache.GetQueryResultAsActionAsync(section, Request("GET", "t"), Sources("B", "A"), Run);
        await cache.GetQueryResultAsActionAsync(section, Request("GET", "t"), Sources("A", "A"), Run);
        Assert.Equal(2, calls);
        await cache.GetQueryResultAsActionAsync(section, Request("GET", "t"), Sources("B", "A"), Run);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("get", true)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    [InlineData("PATCH", false)]
    [InlineData("OPTIONS", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCacheableMethod_OnlyGetAndHead(string? method, bool expected)
    {
        Assert.Equal(expected, CacheService.IsCacheableMethod(method));
    }
}
