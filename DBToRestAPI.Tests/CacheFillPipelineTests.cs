using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// Callers of a cached route who share a key wait on one cache fill. The fill must not depend on
/// the caller who happened to start it: when that caller leaves, the others still get the answer,
/// from the one call the fill made.
/// </summary>
public class CacheFillPipelineTests(CacheFillPipelineTests.FillEngine engine) : IClassFixture<CacheFillPipelineTests.FillEngine>
{
    public sealed class FillEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:fill_query:route"] = "fill/query",
        ["queries:fill_query:verb"] = "GET",
        ["queries:fill_query:cache:memory:duration_in_milliseconds"] = "60000",
        ["queries:fill_query:query"] = """SELECT {http{ {"url": "https://slow.test/query", "method": "GET"} }http} AS resp;""",

        // A query that takes a few seconds by itself, so the caller who started it leaves while it runs.
        ["queries:fill_slow_sql:route"] = "fill/slow-sql",
        ["queries:fill_slow_sql:verb"] = "GET",
        ["queries:fill_slow_sql:cache:memory:duration_in_milliseconds"] = "60000",
        ["queries:fill_slow_sql:query"] =
            "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < 20000000) SELECT count(*) AS n FROM r;",

        ["routes:fill_gateway:route"] = "fill/gateway",
        ["routes:fill_gateway:url"] = "https://slow.test/gateway",
        ["routes:fill_gateway:excluded_headers"] = "host",
        ["routes:fill_gateway:cache:memory:duration_in_milliseconds"] = "60000",

        ["routes:fill_gateway_cookie:route"] = "fill/gateway-cookie",
        ["routes:fill_gateway_cookie:url"] = "https://slow.test/gateway-cookie",
        ["routes:fill_gateway_cookie:excluded_headers"] = "host",
        ["routes:fill_gateway_cookie:cache:memory:duration_in_milliseconds"] = "60000",

        // A gateway GET with a body: streamed when the route has no cache, copied when it does.
        ["routes:fill_gateway_stream:route"] = "fill/gateway-stream",
        ["routes:fill_gateway_stream:url"] = "https://slow.test/stream",
        ["routes:fill_gateway_stream:excluded_headers"] = "host",
        ["routes:fill_gateway_copy:route"] = "fill/gateway-copy",
        ["routes:fill_gateway_copy:url"] = "https://slow.test/copy",
        ["routes:fill_gateway_copy:excluded_headers"] = "host",
        ["routes:fill_gateway_copy:cache:memory:duration_in_milliseconds"] = "60000",

        ["queries:fill_query_alone:route"] = "fill/query-alone",
        ["queries:fill_query_alone:verb"] = "GET",
        ["queries:fill_query_alone:cache:memory:duration_in_milliseconds"] = "60000",
        ["queries:fill_query_alone:query"] = """SELECT {http{ {"url": "https://slow.test/query-alone", "method": "GET"} }http} AS resp;""",

        ["routes:fill_gateway_alone:route"] = "fill/gateway-alone",
        ["routes:fill_gateway_alone:url"] = "https://slow.test/gateway-alone",
        ["routes:fill_gateway_alone:excluded_headers"] = "host",
        ["routes:fill_gateway_alone:cache:memory:duration_in_milliseconds"] = "60000",

        // A caller who leaves: while it waits on a cache fill, and while its own query's call runs.
        ["queries:fill_left_waiting:route"] = "fill/left-waiting",
        ["queries:fill_left_waiting:verb"] = "GET",
        ["queries:fill_left_waiting:cache:memory:duration_in_milliseconds"] = "60000",
        ["queries:fill_left_waiting:query"] = """SELECT {http{ {"url": "https://slow.test/left-waiting", "method": "GET"} }http} AS resp;""",
        ["queries:fill_left_running:route"] = "fill/left-running",
        ["queries:fill_left_running:verb"] = "GET",
        ["queries:fill_left_running:query"] = """SELECT {http{ {"url": "https://slow.test/left-running", "method": "GET"} }http} AS resp;""",
    });

    internal const string QueryEntryLog = "in ApiController.Index method";
    internal const string GatewayEntryLog = "in Step4APIGatewayProcess middleware";

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < 10_000, "timed out");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Starts the caller who starts the fill and, once the fill is running, a second caller who
    /// waits on it; then the first caller leaves, and its request ends. Returns the second call.
    /// </summary>
    internal static async Task<Task<HttpResponseMessage>> SecondCallerAfterTheFirstLeaves(
        PipelineTests.PipelineEngine engine, string route, string entryLog, Task fillRunning)
    {
        var before = engine.Logs.Entries.Count;
        using var leaving = new CancellationTokenSource();
        var first = engine.Client.GetAsync(route, leaving.Token);
        await fillRunning.WaitAsync(TimeSpan.FromSeconds(10));

        // The second caller reaches the cache and waits on that fill.
        var second = engine.Client.GetAsync(route);
        await WaitUntil(() => engine.Logs.Entries.Skip(before).Count(e => e.Message.Contains(entryLog)) >= 2);
        await Task.Delay(300);

        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Task.Delay(300);
        return second;
    }

    [Theory]
    [InlineData("fill/query", "https://slow.test/query", QueryEntryLog)]
    [InlineData("fill/gateway", "https://slow.test/gateway", GatewayEntryLog)]
    public async Task TheCallerWhoStartedTheFillLeaves_TheOthersStillGetTheAnswer(string route, string upstream, string entryLog)
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Http.MapAsync(upstream, async (_, cancel) =>
        {
            called.TrySetResult();
            await release.Task.WaitAsync(cancel);
            return FakeHttpServer.Text("from upstream");
        });

        var second = await SecondCallerAfterTheFirstLeaves(engine, route, entryLog, called.Task);
        release.SetResult();

        using var response = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("from upstream", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, engine.Http.Count(upstream));
    }

    /// <summary>
    /// A caller who leaves is not a failure: one Information line names the route, with no stack
    /// trace, and the engine logs nothing at Warning or above for it.
    /// </summary>
    private async Task AssertLoggedAsLeft(int before, string route)
    {
        var line = $"The caller left before route {route} answered";
        await WaitUntil(() => engine.Logs.Entries.Skip(before).Any(e => e.Message.Contains(line)));
        var entries = engine.Logs.Entries.Skip(before).ToList();
        var left = Assert.Single(entries, e => e.Message.Contains(line));
        Assert.Equal(LogLevel.Information, left.Level);
        Assert.Null(left.Exception);
        Assert.DoesNotContain(entries, e => e.Category.StartsWith("DBToRestAPI") && e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ACallerWhoLeavesWhileWaitingOnAFill_IsLoggedAsLeft_NotAsAnError()
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Http.MapAsync("https://slow.test/left-waiting", async (_, cancel) =>
        {
            called.TrySetResult();
            await release.Task.WaitAsync(cancel);
            return FakeHttpServer.Text("from upstream");
        });
        var before = engine.Logs.Entries.Count;

        var second = await SecondCallerAfterTheFirstLeaves(engine, "fill/left-waiting", QueryEntryLog, called.Task);
        release.SetResult();

        using var response = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertLoggedAsLeft(before, "fill/left-waiting");
    }

    [Fact]
    public async Task ACallerWhoLeavesWhileItsQueryCallRuns_StopsTheCall_AndIsLoggedAsLeft()
    {
        // The call stops with the caller, and the query never runs.
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Http.MapAsync("https://slow.test/left-running", async (_, cancel) =>
        {
            called.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancel);
            }
            catch (OperationCanceledException)
            {
                stopped.TrySetResult();
                throw;
            }
            return FakeHttpServer.Text("never");
        });
        var before = engine.Logs.Entries.Count;

        using var leaving = new CancellationTokenSource();
        var call = engine.Client.GetAsync("fill/left-running", leaving.Token);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertLoggedAsLeft(before, "fill/left-running");
    }

    [Fact]
    public async Task TheCallerWhoStartedASlowQueryLeaves_TheOthersStillGetItsResult()
    {
        // The fill's connection belongs to the fill, not to the response of the caller who started it.
        var before = engine.Logs.Entries.Count;
        var fillRunning = WaitUntil(() => engine.Logs.Entries.Skip(before).Any(e => e.Message.Contains(QueryEntryLog)));

        var second = await SecondCallerAfterTheFirstLeaves(engine, "fill/slow-sql", QueryEntryLog, fillRunning);

        using var response = await second.WaitAsync(TimeSpan.FromSeconds(60));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(20_000_000, JsonDocument.Parse(body).RootElement.GetProperty("n").GetInt64());
    }

    [Fact]
    public async Task ANotStoredGatewayResponse_TheCallerWhoStartedTheFillLeft_TheOthersAreForwardedOnTheirOwn()
    {
        // A response that sets a cookie is never stored. The fill hands it back to the caller who
        // started it, who has gone, and each caller who waited is forwarded on its own.
        const string upstream = "https://slow.test/gateway-cookie";
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Http.MapAsync(upstream, async (_, cancel) =>
        {
            if (called.TrySetResult())
                await release.Task.WaitAsync(cancel);
            var answer = FakeHttpServer.Text("from upstream");
            answer.Headers.Add("Set-Cookie", "session=1; Path=/");
            return answer;
        });

        var second = await SecondCallerAfterTheFirstLeaves(engine, "fill/gateway-cookie", GatewayEntryLog, called.Task);
        release.SetResult();

        using var response = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("from upstream", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, engine.Http.Count(upstream));
    }

    [Theory]
    [InlineData("fill/gateway-stream", "https://slow.test/stream", false)]  // forwarded within its own request
    [InlineData("fill/gateway-copy", "https://slow.test/copy", true)]       // a cache fill may forward it later
    public async Task AGatewayGetBody_IsCopiedOnlyWhenACacheFillMayForwardIt(string route, string upstream, bool copied)
    {
        Type? contentType = null;
        string? body = null;
        engine.Http.MapRequest(upstream, async (message, cancel) =>
        {
            contentType = message.Content?.GetType();
            body = message.Content == null ? null : await message.Content.ReadAsStringAsync(cancel);
            return FakeHttpServer.Text("ok");
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, route)
        {
            Content = new StringContent("a body", System.Text.Encoding.UTF8, "application/octet-stream"),
        };
        using var response = await engine.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a body", body);
        Assert.Equal(copied ? typeof(ByteArrayContent) : typeof(StreamContent), contentType);
    }

    [Theory]
    [InlineData("fill/query-alone", "https://slow.test/query-alone")]
    [InlineData("fill/gateway-alone", "https://slow.test/gateway-alone")]
    public async Task EveryCallerLeaves_TheFillStops(string route, string upstream)
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Http.MapAsync(upstream, async (_, cancel) =>
        {
            called.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancel);
            }
            catch (OperationCanceledException)
            {
                stopped.TrySetResult();
                throw;
            }
            return FakeHttpServer.Text("never");
        });

        using var leaving = new CancellationTokenSource();
        var only = engine.Client.GetAsync(route, leaving.Token);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        leaving.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>
/// As <see cref="CacheFillPipelineTests"/>, on Kestrel, where a request's body is gone once the
/// request has ended. A gateway fill forwards the body of the caller who started it, and sends it
/// only once the connection to the upstream is up.
/// </summary>
public class CacheFillKestrelTests(CacheFillKestrelTests.KestrelFillEngine engine) : IClassFixture<CacheFillKestrelTests.KestrelFillEngine>
{
    public sealed class KestrelFillEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["routes:fill_gateway_body:route"] = "fill/gateway-body",
        ["routes:fill_gateway_body:url"] = "https://slow.test/gateway-body",
        ["routes:fill_gateway_body:excluded_headers"] = "host",
        ["routes:fill_gateway_body:cache:memory:duration_in_milliseconds"] = "60000",
    }, kestrel: true);

    [Fact]
    public async Task TheCallerWhoStartedAGatewayFillLeavesBeforeItsBodyIsSent_TheOthersStillGetTheAnswer()
    {
        const string upstream = "https://slow.test/gateway-body";
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Http.MapRequest(upstream, async (message, cancel) =>
        {
            called.TrySetResult();
            await release.Task.WaitAsync(cancel);
            if (message.Content != null)
                await message.Content.ReadAsByteArrayAsync(cancel);
            return FakeHttpServer.Text("from upstream");
        });

        var second = await CacheFillPipelineTests.SecondCallerAfterTheFirstLeaves(
            engine, "fill/gateway-body", CacheFillPipelineTests.GatewayEntryLog, called.Task);
        release.SetResult();

        using var response = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("from upstream", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, engine.Http.Count(upstream));
    }
}
