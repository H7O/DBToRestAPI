using System.Text.Json;
using Com.H.Data.Common;
using DBToRestAPI.Cache;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
