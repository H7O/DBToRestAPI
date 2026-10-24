using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// A gateway route whose target fails: 502 when the target can't be reached or its answer can't be
/// read, 504 when it doesn't answer within target_route_timeout_seconds, each logged with its target.
/// They used to be the generic 400, with nothing logged. The calls go through the engine's real
/// handlers, to an upstream on a loopback port and to a host that doesn't exist.
/// </summary>
public class GatewayFailureTests(GatewayFailureTests.Engine engine) : IClassFixture<GatewayFailureTests.Engine>
{
    public sealed class Upstream : IDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        public Upstream()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Configuration.Sources.Clear();   // not the engine's appsettings files beside the tests
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.MapGet("/slow", async (HttpContext context) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
                return "late";
            });
            // Closes the connection without an answer.
            _app.MapGet("/drop", (HttpContext context) => context.Abort());
            // Written by the handler: a lambda taking only the HttpContext is a RequestDelegate, whose
            // returned value would be dropped.
            _app.MapPost("/echo", async (HttpContext context) =>
                await context.Response.WriteAsync(await new StreamReader(context.Request.Body).ReadToEndAsync()));
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
            ["max_payload_size_in_bytes"] = "1000",

            ["routes:gw_no_host:route"] = "gw/no_host/*",
            ["routes:gw_no_host:url"] = "http://gateway-target.invalid/",

            ["routes:gw_drop:route"] = "gw/drop",
            ["routes:gw_drop:url"] = upstream.BaseUrl + "/drop",
            ["routes:gw_drop:excluded_headers"] = "host",

            ["routes:gw_drop_cached:route"] = "gw/drop_cached",
            ["routes:gw_drop_cached:url"] = upstream.BaseUrl + "/drop",
            ["routes:gw_drop_cached:excluded_headers"] = "host",
            ["routes:gw_drop_cached:cache:memory:duration_in_milliseconds"] = "60000",

            ["routes:gw_slow:route"] = "gw/slow",
            ["routes:gw_slow:url"] = upstream.BaseUrl + "/slow",
            ["routes:gw_slow:excluded_headers"] = "host",
            ["routes:gw_slow:target_route_timeout_seconds"] = "1",

            ["routes:gw_echo:route"] = "gw/echo",
            ["routes:gw_echo:url"] = upstream.BaseUrl + "/echo",
            ["routes:gw_echo:excluded_headers"] = "host",
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

    private async Task<(HttpStatusCode Status, string Message, List<CapturedLogs.Entry> Logged)> SendAsync(HttpRequestMessage request)
    {
        var before = engine.Logs.Entries.Count;
        using var response = await engine.Client.SendAsync(request);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());
        return (response.StatusCode, body.GetProperty("message").GetString()!, engine.Logs.Entries.Skip(before).ToList());
    }

    private Task<(HttpStatusCode Status, string Message, List<CapturedLogs.Entry> Logged)> GetAsync(string path)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    private static void AssertLogged(List<CapturedLogs.Entry> logged, string route, int status, string target)
    {
        var entry = Assert.Single(logged, e => e.Message.Contains($"Gateway route {route} answered {status}: "));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains($"Target: {target}", entry.Message);
    }

    [Theory]
    [InlineData("gw/no_host/items?key=secret", "gw/no_host/items", "http://gateway-target.invalid/items")]
    [InlineData("gw/drop", "gw/drop", null)]
    [InlineData("gw/drop_cached", "gw/drop_cached", null)]
    public async Task ATargetThatCantBeReached_Is502_AndLogged(string path, string route, string? target)
    {
        var (status, message, logged) = await GetAsync(path);

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("An error occurred while processing your request.", message);
        // The target without its query string, which may hold a key.
        AssertLogged(logged, route, 502, target ?? engine.Upstream.BaseUrl + "/drop");
        Assert.DoesNotContain(logged, e => e.Message.Contains("secret"));
    }

    [Fact]
    public async Task ATargetThatDoesntAnswerInTime_Is504_AndLogged()
    {
        var watch = Stopwatch.StartNew();

        var (status, message, logged) = await GetAsync("gw/slow");

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
        Assert.Equal("An error occurred while processing your request.", message);
        AssertLogged(logged, "gw/slow", 504, engine.Upstream.BaseUrl + "/slow");
    }

    [Fact]
    public async Task ACallersBodyTooLargeToSend_KeepsIts413()
    {
        // A text body isn't read before it is forwarded, so the server refuses it while the client
        // sends it, inside the client's own exception.
        var request = new HttpRequestMessage(HttpMethod.Post, "gw/echo")
        {
            Content = new StringContent(new string('x', 2000), Encoding.UTF8, "text/plain")
        };

        var (status, message, logged) = await SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("The request body could not be read.", message);
        Assert.DoesNotContain(logged, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task ABodyWithinTheLimit_IsForwarded()
    {
        using var response = await engine.Client.PostAsync("gw/echo", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }
}
