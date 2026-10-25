using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// A gateway route without a cache passes the target's answer on as it arrives, rather than reading
/// it whole into memory first. target_route_timeout_seconds covers the wait for the target's headers,
/// then each wait for more of its answer. A target that fails before its answer has started gets its
/// caller the failure's status; after, the connection is cut, so the caller can't take a cut-off
/// answer for a whole one. A route with a cache reads the answer whole, to store it, within the
/// timeout. The calls go through the engine's real handlers to an upstream on a loopback port.
/// </summary>
public class GatewayStreamingTests(GatewayStreamingTests.Engine engine) : IClassFixture<GatewayStreamingTests.Engine>
{
    public sealed class Upstream : IDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        /// <summary>Released by the test once it has read the first part of /gated.</summary>
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Upstream()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Configuration.Sources.Clear();   // not the engine's appsettings files beside the tests
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();

            async Task Send(HttpContext context, string text)
            {
                await context.Response.WriteAsync(text, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }

            _app.MapGet("/gated", async (HttpContext context) =>
            {
                context.Response.Headers["X-Target"] = "gated";
                await Send(context, "first");
                await Gate.Task.WaitAsync(TimeSpan.FromSeconds(10), context.RequestAborted);
                await Send(context, "second");
            });
            _app.MapGet("/break-after-part", async (HttpContext context) =>
            {
                await Send(context, "part");
                await Task.Delay(100);
                context.Abort();
            });
            _app.MapGet("/stall-after-part", async (HttpContext context) =>
            {
                await Send(context, "part");
                await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            });
            // Its headers, then nothing.
            _app.MapGet("/headers-then-stall", async (HttpContext context) =>
            {
                context.Response.Headers["X-Target"] = "stalled";
                await context.Response.StartAsync(context.RequestAborted);
                // Sent now: Kestrel holds a started response's headers until a write or a flush.
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            });
            // Seven blocks, 500 ms apart: three seconds in all, and never half a second without data.
            _app.MapGet("/steady", async (HttpContext context) =>
            {
                for (var i = 0; i < 7; i++)
                {
                    if (i > 0)
                        await Task.Delay(500, context.RequestAborted);
                    await Send(context, "x");
                }
            });
            _app.MapGet("/empty", (HttpContext context) =>
            {
                context.Response.StatusCode = 204;
                return Task.CompletedTask;
            });
            _app.StartAsync().GetAwaiter().GetResult();
            BaseUrl = _app.Urls.First().TrimEnd('/');
        }

        public void Dispose() => _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public sealed class Engine : PipelineTests.PipelineEngine
    {
        public Upstream Upstream { get; }

        public Engine() : this(new Upstream()) { }

        private Engine(Upstream upstream) : base(_ => new()
        {
            ["routes:gs:route"] = "gs/*",
            ["routes:gs:url"] = upstream.BaseUrl + "/",
            ["routes:gs:excluded_headers"] = "host",

            ["routes:gs1:route"] = "gs1/*",
            ["routes:gs1:url"] = upstream.BaseUrl + "/",
            ["routes:gs1:excluded_headers"] = "host",
            ["routes:gs1:target_route_timeout_seconds"] = "1",

            ["routes:gs2:route"] = "gs2/*",
            ["routes:gs2:url"] = upstream.BaseUrl + "/",
            ["routes:gs2:excluded_headers"] = "host",
            ["routes:gs2:target_route_timeout_seconds"] = "2",

            ["routes:gsc:route"] = "gsc/*",
            ["routes:gsc:url"] = upstream.BaseUrl + "/",
            ["routes:gsc:excluded_headers"] = "host",
            ["routes:gsc:target_route_timeout_seconds"] = "1",
            ["routes:gsc:cache:memory:duration_in_milliseconds"] = "60000",
        }, kestrel: true, fakeHttp: false)
        {
            Upstream = upstream;
        }

        public override void Dispose()
        {
            base.Dispose();
            Upstream.Dispose();
        }
    }

    private Task<HttpResponseMessage> GetHeadersAsync(string path)
        => engine.Client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);

    private static async Task<string> ReadAsync(Stream body, int length)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = new byte[length];
        await body.ReadExactlyAsync(bytes, stop.Token);
        return Encoding.UTF8.GetString(bytes);
    }

    private List<CapturedLogs.Entry> LoggedSince(int before) => engine.Logs.Entries.Skip(before).ToList();

    [Fact]
    public async Task TheAnswer_ReachesTheCallerAsItArrives()
    {
        // The target sends "second" only once the caller has "first": an engine that read the whole
        // answer before passing it on would never give the caller "first".
        using var response = await GetHeadersAsync("gs/gated");
        await using var body = await response.Content.ReadAsStreamAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gated", string.Join(",", response.Headers.GetValues("X-Target")));
        Assert.Equal("first", await ReadAsync(body, 5));
        engine.Upstream.Gate.SetResult();
        Assert.Equal("second", await new StreamReader(body).ReadToEndAsync());
    }

    [Fact]
    public async Task ATargetThatBreaksOffItsAnswer_CutsTheCallersConnection()
    {
        var before = engine.Logs.Entries.Count;
        using var response = await GetHeadersAsync("gs/break-after-part");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => response.Content.ReadAsStringAsync());
        var entry = Assert.Single(LoggedSince(before), e => e.Message.Contains("Gateway route gs/break-after-part cut its caller's connection: its target's answer broke off"));
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public async Task ATargetThatStopsSending_IsCutOffAfterTheTimeout()
    {
        var before = engine.Logs.Entries.Count;
        var watch = Stopwatch.StartNew();
        using var response = await GetHeadersAsync("gs1/stall-after-part");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => response.Content.ReadAsStringAsync());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
        Assert.Contains(LoggedSince(before), e => e.Message.Contains(
            "Gateway route gs1/stall-after-part cut its caller's connection: its target didn't send the rest of its answer within target_route_timeout_seconds"));
    }

    [Fact]
    public async Task ACallerWhoLeavesMidAnswer_IsLoggedOnce_AsInformation()
    {
        var before = engine.Logs.Entries.Count;
        using (var response = await GetHeadersAsync("gs/stall-after-part"))
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            Assert.Equal("part", await ReadAsync(body, 4));
        }

        // The engine notices once its wait for the target is cancelled with the request.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !LoggedSince(before).Any(e => e.Message.Contains("The caller left before route gs/stall-after-part answered")))
            await Task.Delay(50);
        var entry = Assert.Single(LoggedSince(before), e => e.Message.Contains("The caller left before route gs/stall-after-part answered"));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.DoesNotContain(LoggedSince(before), e => e.Level >= LogLevel.Error && e.Message.Contains("gs/stall-after-part"));
    }

    [Fact]
    public async Task ATargetThatKeepsSending_IsNotCutOff_ThoughItTakesLongerThanTheTimeout()
    {
        using var response = await engine.Client.GetAsync("gs2/steady");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("xxxxxxx", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ACachedRoute_ReadsTheWholeAnswerWithinTheTimeout()
    {
        // Read whole to be stored, so the three seconds of /steady are over the route's one.
        using var response = await engine.Client.GetAsync("gsc/steady");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task ATargetThatSendsNothingAfterItsHeaders_Is504_WithoutThem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "gs1/headers-then-stall");
        request.Headers.Add("Origin", "https://app.example.com");

        using var response = await engine.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Target"));
        // The engine's own headers, such as CORS's, are kept.
        Assert.Equal("https://app.example.com", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("\"success\":false", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnAnswerWithoutABody_KeepsItsStatus()
    {
        using var response = await engine.Client.GetAsync("gs/empty");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}

/// <summary>
/// A target whose answer fails on its first read, after its headers: nothing has been sent, so the
/// caller gets a 502 without the target's headers. A fake target, so that the failure comes after
/// the headers every time.
/// </summary>
public class GatewayAnswerReadFailureTests(GatewayAnswerReadFailureTests.Engine engine) : IClassFixture<GatewayAnswerReadFailureTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["routes:gf:route"] = "gf/*",
        ["routes:gf:url"] = "https://upstream.test/",
    });

    private sealed class BrokenStream : MemoryStream
    {
        private static IOException Failure => new("The connection was reset.");
        public override int Read(byte[] buffer, int offset, int count) => throw Failure;
        public override int Read(Span<byte> buffer) => throw Failure;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Failure;
    }

    [Fact]
    public async Task AnAnswerThatFailsBeforeItsFirstBlock_Is502_WithoutItsHeaders()
    {
        engine.Http.Map("https://upstream.test/broken", _ =>
        {
            var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
            answer.Headers.Add("X-Target", "broken");
            return answer;
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "gf/broken");
        request.Headers.Add("Origin", "https://app.example.com");

        using var response = await engine.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Target"));
        Assert.Equal("https://app.example.com", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Origin")));
    }
}
