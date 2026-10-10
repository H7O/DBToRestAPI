using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// The engine's HTTP clients are pooled and serve every caller, so none of them may keep a cookie: a
/// remote service's Set-Cookie for one caller would go out with the next caller's request. These calls
/// go through the engine's real handlers to an upstream on a loopback port that sets a new session
/// cookie on every response and records the Cookie header it receives.
/// </summary>
public class CookiePipelineTests(CookiePipelineTests.CookieEngine engine) : IClassFixture<CookiePipelineTests.CookieEngine>
{
    public sealed class Upstream : IDisposable
    {
        private readonly WebApplication _app;
        public ConcurrentQueue<string> CookiesReceived { get; } = new();
        public string BaseUrl { get; }

        public Upstream()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Configuration.Sources.Clear();   // not the engine's appsettings files beside the tests
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.MapGet("/visit", (HttpContext context) =>
            {
                CookiesReceived.Enqueue(context.Request.Headers.Cookie.ToString());
                context.Response.Headers.SetCookie = $"session={Guid.NewGuid():N}; Path=/";
                return "visited";
            });
            _app.StartAsync().GetAwaiter().GetResult();
            BaseUrl = _app.Urls.First().TrimEnd('/');
        }

        public void Dispose() => _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public sealed class CookieEngine : PipelineTests.PipelineEngine
    {
        public Upstream Upstream { get; }

        public CookieEngine() : this(new Upstream()) { }

        private CookieEngine(Upstream upstream) : base(_ => new()
        {
            ["queries:ck_file:route"] = "ck/file",
            ["queries:ck_file:verb"] = "GET",
            ["queries:ck_file:response_structure"] = "file",
            ["queries:ck_file:query"] = $"SELECT 'a.txt' AS file_name, '{upstream.BaseUrl}/visit' AS http;",

            ["queries:ck_marker:route"] = "ck/marker",
            ["queries:ck_marker:verb"] = "GET",
            ["queries:ck_marker:query"] = $$"""SELECT {http{ {"url": "{{upstream.BaseUrl}}/visit", "method": "GET"} }http} AS resp;""",

            // The clients that ignore certificate errors build their own handler.
            ["queries:ck_marker_insecure:route"] = "ck/marker_insecure",
            ["queries:ck_marker_insecure:verb"] = "GET",
            ["queries:ck_marker_insecure:query"] =
                $$"""SELECT {http{ {"url": "{{upstream.BaseUrl}}/visit", "method": "GET", "ignore_certificate_errors": true} }http} AS resp;""",

            ["routes:ck_gw:route"] = "ck/gw/*",
            ["routes:ck_gw:url"] = upstream.BaseUrl + "/",
            ["routes:ck_gw:excluded_headers"] = "host",

            ["routes:ck_gw_cached:route"] = "ck/gwc/*",
            ["routes:ck_gw_cached:url"] = upstream.BaseUrl + "/",
            ["routes:ck_gw_cached:excluded_headers"] = "host",
            ["routes:ck_gw_cached:cache:memory:duration_in_milliseconds"] = "60000",

            ["routes:ck_gw_insecure:route"] = "ck/gwi/*",
            ["routes:ck_gw_insecure:url"] = upstream.BaseUrl + "/",
            ["routes:ck_gw_insecure:excluded_headers"] = "host",
            ["routes:ck_gw_insecure:ignore_target_route_certificate_errors"] = "true",
        }, fakeHttp: false)
        {
            Upstream = upstream;
        }

        public override void Dispose()
        {
            base.Dispose();
            Upstream.Dispose();
        }
    }

    private async Task<string[]> CookiesSentByTwoCallsAsync(string path, string? callerCookie = null)
    {
        var before = engine.Upstream.CookiesReceived.Count;
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (callerCookie != null)
                request.Headers.Add("Cookie", callerCookie);
            using var response = await engine.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        return engine.Upstream.CookiesReceived.Skip(before).ToArray();
    }

    [Theory]
    [InlineData("ck/file")]      // a download's http source
    [InlineData("ck/marker")]    // an {http{...}} call
    [InlineData("ck/gw/visit")]  // an API gateway route
    [InlineData("ck/marker_insecure")]
    [InlineData("ck/gwi/visit")]
    public async Task ACookieTheUpstreamSet_IsNotSentWithTheNextCall(string path)
    {
        var sent = await CookiesSentByTwoCallsAsync(path);

        Assert.Equal(["", ""], sent);
    }

    [Fact]
    public async Task CachedGateway_AResponseWithSetCookie_IsNotStoredForTheNextCaller()
    {
        var before = engine.Upstream.CookiesReceived.Count;
        var cookies = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            using var response = await engine.Client.GetAsync("ck/gwc/visit");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            cookies.Add(string.Join(";", response.Headers.GetValues("Set-Cookie")));
        }

        // Both calls reached the target, and each caller got its own cookie.
        Assert.Equal(before + 2, engine.Upstream.CookiesReceived.Count);
        Assert.NotEqual(cookies[0], cookies[1]);
    }

    [Fact]
    public async Task Gateway_ACallersOwnCookie_IsForwardedAlone()
    {
        var sent = await CookiesSentByTwoCallsAsync("ck/gw/visit", callerCookie: "mine=1");

        Assert.Equal(["mine=1", "mine=1"], sent);
    }
}
