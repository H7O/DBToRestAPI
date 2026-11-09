using System.Net;
using System.Text.Json;
using Com.H.Data.Common;
using DBToRestAPI.Cache;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// The largest answer a route's cache stores: <c>cache:memory:max_entry_size_in_bytes</c> on the route,
/// else in settings.xml, else 1 MiB. Read for each cache fill, so a change takes effect without a
/// restart.
///
/// Before it, HybridCache's own limit applied: 1 MiB, read once when the cache was created, and set
/// nowhere. A route whose answer was larger ran its query on every request, and each one logged
/// "Cache MaximumPayloadBytes (1048576) exceeded" without naming the route.
/// </summary>
public class CacheEntrySizeTests
{
    private const string MarkerRegex = @"(?<open_marker>\{\{)(?<param>.*?)?(?<close_marker>\}\})";
    private const string Setting = "cache:memory:max_entry_size_in_bytes";
    private const long OneMiB = 1024 * 1024;

    private static (CacheService Cache, IConfigurationRoot Config, CapturedLogs Logs) Create(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logs = new CapturedLogs();
        var services = new ServiceCollection();
        // HybridCache's own lines too, such as its MaximumPayloadBytes error.
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddHybridCache(CacheService.ConfigureHybridCache);
        var sp = services.BuildServiceProvider();
        var logger = new Logger<CacheService>(LoggerFactory.Create(builder => builder.AddProvider(logs)));
        return (new CacheService(new TestEncryptedConfiguration(config), sp, sp.GetRequiredService<HybridCache>(), logger), config, logs);
    }

    /// <summary>A route with a 60-second cache, and its own limit when one is given.</summary>
    private static Dictionary<string, string?> Route(string name, long? limit = null, string prefix = "queries")
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{prefix}:{name}:cache:memory:duration_in_milliseconds"] = "60000",
        };
        if (limit != null)
            settings[$"{prefix}:{name}:{Setting}"] = limit.ToString();
        return settings;
    }

    private static Dictionary<string, string?> Merge(params Dictionary<string, string?>[] parts)
        => parts.SelectMany(p => p).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static HttpContext Get()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        return context;
    }

    /// <summary>
    /// A query route whose answer is <c>{"call":n,"pad":"xx..."}</c>, <see cref="Size"/> bytes of
    /// JSON. The call number shows whether an answer came from the cache.
    /// </summary>
    private sealed class QueryEndpoint(long size)
    {
        // {"call":1,"pad":""}
        private const int Overhead = 19;
        public int Calls;
        public long Size => size;

        public async Task<int> CallAsync(CacheService cache, IConfigurationSection section, Task? gate = null)
        {
            var result = await cache.GetQueryResultAsActionAsync(section, Get(),
                [new() { DataModel = new Dictionary<string, object>(), QueryParamsRegex = MarkerRegex }],
                async (materialise, cancel) =>
                {
                    var call = Interlocked.Increment(ref Calls);
                    if (gate != null)
                        await gate;
                    return new ObjectResult(new { call, pad = new string('x', (int)(size - Overhead)) }) { StatusCode = 200 };
                });
            var json = (JsonElement)((ObjectResult)result).Value!;
            Assert.Equal(size, JsonSerializer.SerializeToUtf8Bytes(json).Length);
            return json.GetProperty("call").GetInt32();
        }
    }

    /// <summary>A gateway route whose response body is <c>size</c> bytes, each the call number.</summary>
    private sealed class GatewayEndpoint(int size)
    {
        public int Calls;

        public async Task<int> CallAsync(CacheService cache, IConfigurationSection section, Task? gate = null)
        {
            var response = await cache.GetForGateway<CachableHttpResponseContainer>(section, Get(), "gw",
                async (buffer, cancel) =>
                {
                    var call = Interlocked.Increment(ref Calls);
                    if (gate != null)
                        await gate;
                    return new CachableHttpResponseContainer { StatusCode = 200, Content = Enumerable.Repeat((byte)call, size).ToArray() };
                });
            Assert.Equal(size, response!.Content.Length);
            return response.Content[0];
        }
    }

    [Fact]
    public async Task AnAnswerAtTheLimit_IsStored_OneByteLarger_IsNot()
    {
        var (cache, config, _) = Create(Merge(Route("at", 500), Route("over", 499)));
        var endpoint = new QueryEndpoint(500);

        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:at")));
        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:at")));

        Assert.Equal(2, await endpoint.CallAsync(cache, config.GetSection("queries:over")));
        Assert.Equal(3, await endpoint.CallAsync(cache, config.GetSection("queries:over")));
    }

    [Fact]
    public async Task WithoutTheSetting_TheLimitIsOneMiB()
    {
        var (cache, config, _) = Create(Merge(Route("at"), Route("over")));

        var atTheLimit = new QueryEndpoint(OneMiB);
        Assert.Equal(1, await atTheLimit.CallAsync(cache, config.GetSection("queries:at")));
        Assert.Equal(1, await atTheLimit.CallAsync(cache, config.GetSection("queries:at")));

        var over = new QueryEndpoint(OneMiB + 1);
        Assert.Equal(1, await over.CallAsync(cache, config.GetSection("queries:over")));
        Assert.Equal(2, await over.CallAsync(cache, config.GetSection("queries:over")));
    }

    [Fact]
    public async Task TheGlobalSetting_RaisesTheLimitAboveHybridCachesOwn()
    {
        // HybridCache's own limit is 1 MiB unless ConfigureHybridCache lifts it. Over it, HybridCache
        // logs an error on every fill (10.11.0 still keeps the entry in memory; earlier versions didn't).
        var (cache, config, logs) = Create(Merge(Route("big"), new() { [Setting] = (4 * OneMiB).ToString() }));
        var endpoint = new QueryEndpoint(3 * OneMiB);

        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:big")));
        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:big")));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("MaximumPayloadBytes"));
    }

    [Fact]
    public async Task TheRoutesSetting_WinsOverTheGlobalOne()
    {
        var (cache, config, _) = Create(Merge(Route("own", 1000), Route("global"), new() { [Setting] = "100" }));
        var endpoint = new QueryEndpoint(500);

        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:own")));
        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:own")));

        Assert.Equal(2, await endpoint.CallAsync(cache, config.GetSection("queries:global")));
        Assert.Equal(3, await endpoint.CallAsync(cache, config.GetSection("queries:global")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task AValueUnderOne_CountsAsNotSet(string value)
    {
        // The route's 0 falls through to the global 100, which the 500-byte answer is over.
        var settings = Merge(Route("r"), new() { [Setting] = "100" });
        settings[$"queries:r:{Setting}"] = value;
        var (cache, config, _) = Create(settings);
        var endpoint = new QueryEndpoint(500);

        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("queries:r")));
        Assert.Equal(2, await endpoint.CallAsync(cache, config.GetSection("queries:r")));
    }

    [Fact]
    public async Task AValueThatIsNotANumber_IsAnInvalidSetting()
    {
        var settings = Route("r");
        settings[$"queries:r:{Setting}"] = "4MB";
        var (cache, config, _) = Create(settings);

        var ex = await Assert.ThrowsAsync<InvalidSettingException>(
            () => new QueryEndpoint(500).CallAsync(cache, config.GetSection("queries:r")));
        Assert.Equal(Setting, ex.Setting);
        Assert.Equal("4MB", ex.Value);
    }

    [Fact]
    public async Task AChangedLimit_AppliesToTheNextFill_WithoutARestart()
    {
        var (cache, config, _) = Create(Merge(Route("r"), new() { [Setting] = "100" }));
        var section = config.GetSection("queries:r");
        var endpoint = new QueryEndpoint(500);

        Assert.Equal(1, await endpoint.CallAsync(cache, section));
        Assert.Equal(2, await endpoint.CallAsync(cache, section));

        config[Setting] = "1000";
        config.Reload();
        Assert.Equal(3, await endpoint.CallAsync(cache, section));
        Assert.Equal(3, await endpoint.CallAsync(cache, section));

        // A lower limit doesn't remove what is stored: the entry stays until it expires.
        config[Setting] = "100";
        config.Reload();
        Assert.Equal(3, await endpoint.CallAsync(cache, section));
    }

    [Fact]
    public async Task CallersWaitingOnAFill_GetItsAnswer_EvenWhenItIsTooLargeToStore()
    {
        var (cache, config, _) = Create(Route("r", 100));
        var section = config.GetSection("queries:r");
        var endpoint = new QueryEndpoint(500);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The second call finds the first one's fill running, and waits on it.
        var first = endpoint.CallAsync(cache, section, gate.Task);
        var second = endpoint.CallAsync(cache, section, gate.Task);
        gate.SetResult();

        Assert.Equal(1, await first);
        Assert.Equal(1, await second);
        Assert.Equal(1, endpoint.Calls);

        // Nothing was stored.
        Assert.Equal(2, await endpoint.CallAsync(cache, section));
    }

    [Fact]
    public async Task ATooLargeAnswer_IsLoggedOncePerFill_WithItsRouteSizeAndLimit()
    {
        var (cache, config, logs) = Create(Route("r", 100));
        var section = config.GetSection("queries:r");
        var endpoint = new QueryEndpoint(500);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = endpoint.CallAsync(cache, section, gate.Task);
        var second = endpoint.CallAsync(cache, section, gate.Task);
        gate.SetResult();
        await Task.WhenAll(first, second);

        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("queries:r", warning.Message);
        Assert.Contains("500 bytes", warning.Message);
        Assert.Contains("100 bytes", warning.Message);
        Assert.Contains(Setting, warning.Message);
    }

    [Fact]
    public async Task AGatewayResponse_IsMeasuredByItsBody()
    {
        var (cache, config, _) = Create(Merge(Route("at", 100, "routes"), Route("over", 99, "routes")));
        var endpoint = new GatewayEndpoint(100);

        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("routes:at")));
        Assert.Equal(1, await endpoint.CallAsync(cache, config.GetSection("routes:at")));

        Assert.Equal(2, await endpoint.CallAsync(cache, config.GetSection("routes:over")));
        Assert.Equal(3, await endpoint.CallAsync(cache, config.GetSection("routes:over")));
    }

    [Fact]
    public async Task CallersWaitingOnAGatewayFill_GetItsResponse_EvenWhenItIsTooLargeToStore()
    {
        var (cache, config, _) = Create(Route("r", 10, "routes"));
        var section = config.GetSection("routes:r");
        var endpoint = new GatewayEndpoint(100);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = endpoint.CallAsync(cache, section, gate.Task);
        var second = endpoint.CallAsync(cache, section, gate.Task);
        gate.SetResult();

        Assert.Equal(1, await first);
        Assert.Equal(1, await second);
        Assert.Equal(1, endpoint.Calls);
        Assert.Equal(2, await endpoint.CallAsync(cache, section));
    }

    /// <summary>The engine itself: its configuration, its HybridCache and its logger.</summary>
    private sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        [Setting] = "500",
        ["queries:sized:route"] = "sized",
        ["queries:sized:verb"] = "GET",
        ["queries:sized:cache:memory:duration_in_milliseconds"] = "60000",
        // About 1,030 bytes of JSON, and a different r on every run of the query.
        ["queries:sized:query"] = "SELECT random() AS r, substr(hex(zeroblob(1000)), 1, 1000) AS pad;",
    });

    [Fact]
    public async Task TheEngine_AppliesAChangedLimit_WithoutARestart()
    {
        using var engine = new Engine();
        async Task<string> GetAsync()
        {
            using var response = await engine.Client.GetAsync("sized");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}");
            return body;
        }

        // Two fills, each over the limit and logged.
        Assert.NotEqual(await GetAsync(), await GetAsync());
        var warnings = engine.Logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains(Setting)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("queries:sized", warnings[0].Message);

        var configuration = (IConfigurationRoot)engine.Services.GetRequiredService<IConfiguration>();
        configuration[Setting] = "5000";
        configuration.Reload();

        var stored = await GetAsync();
        Assert.Equal(stored, await GetAsync());
    }
}
