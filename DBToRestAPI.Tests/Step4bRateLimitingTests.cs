using System.Net;
using System.Text.Json;
using DBToRestAPI.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// Behaviour of <see cref="Step4bRateLimiting"/> in the pipeline: which request is the first to be
/// rejected, what the 429 looks like, who shares an allowance, and that a configuration fault or a
/// fault inside the decision fails open rather than failing every request.
/// </summary>
public class Step4bRateLimitingTests
{
    // ── harness ───────────────────────────────────────────────────────────────────────────────

    /// <summary>An in-memory provider whose keys can be removed, to simulate deleting a block on hot reload.</summary>
    private sealed class RemovableMemoryProvider(Dictionary<string, string?> data)
        : MemoryConfigurationProvider(new MemoryConfigurationSource { InitialData = data })
    {
        public void Remove(string key) => Data.Remove(key);
    }

    private sealed class RemovableMemorySource(RemovableMemoryProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    /// <summary>Captures log entries so tests can assert on levels and counts.</summary>
    private sealed class ListLogger : ILogger<Step4bRateLimiting>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
        public int Count(LogLevel level) { lock (Entries) return Entries.Count(e => e.Level == level); }
    }

    private sealed class Harness
    {
        public Step4bRateLimiting Middleware = null!;
        public IConfigurationRoot Config = null!;
        public RemovableMemoryProvider Provider = null!;
        public ListLogger Log = new();
        public int NextCalls;
    }

    private static Harness Create(Dictionary<string, string?> data)
    {
        var h = new Harness { Provider = new RemovableMemoryProvider(data) };
        h.Config = new ConfigurationBuilder().Add(new RemovableMemorySource(h.Provider)).Build();
        RequestDelegate next = _ => { h.NextCalls++; return Task.CompletedTask; };
        h.Middleware = new Step4bRateLimiting(next, h.Log, new TestEncryptedConfiguration(h.Config));
        return h;
    }

    private static DefaultHttpContext Request(Harness h, string endpoint = "ep", string? user = null, string ip = "10.0.0.1", Action<DefaultHttpContext>? setup = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["section"] = h.Config.GetSection($"queries:{endpoint}");
        ctx.Items["route"] = endpoint;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Response.Body = new MemoryStream();
        if (user is not null)
            ctx.Items["user_claims"] = new Dictionary<string, object> { ["user_id"] = user };
        setup?.Invoke(ctx);
        return ctx;
    }

    private static async Task<int> Send(Harness h, DefaultHttpContext ctx)
    {
        await h.Middleware.InvokeAsync(ctx);
        // The engine writes JSON to the response pipe; the server flushes it at the end of the
        // request, so a test that wants to read the body has to flush it here.
        await ctx.Response.BodyWriter.FlushAsync();
        return ctx.Response.StatusCode;
    }

    private static async Task<List<int>> Burst(Harness h, int n, Func<DefaultHttpContext> make)
    {
        var codes = new List<int>();
        for (var i = 0; i < n; i++)
            codes.Add(await Send(h, make()));
        return codes;
    }

    private static JsonElement Body(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(ctx.Response.Body);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string?> Endpoint(int max, int? window = null, params (string key, string value)[] extra)
    {
        var d = new Dictionary<string, string?> { ["queries:ep:rate_limit:max_requests"] = max.ToString() };
        if (window is not null) d["queries:ep:rate_limit:window_seconds"] = window.ToString();
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    // ── the basics ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoBlockAnywhere_EveryRequestReachesNext()
    {
        var h = Create(new() { ["queries:ep:route"] = "x" });

        var codes = await Burst(h, 50, () => Request(h, user: "u1"));

        Assert.All(codes, c => Assert.Equal(200, c));
        Assert.Equal(50, h.NextCalls);
    }

    [Fact]
    public async Task EndpointLimit_FirstMaxPass_NextIs429_WithRetryAfterAndBody()
    {
        var h = Create(Endpoint(3, 10));

        Assert.Equal(200, await Send(h, Request(h, user: "u1")));
        Assert.Equal(200, await Send(h, Request(h, user: "u1")));
        Assert.Equal(200, await Send(h, Request(h, user: "u1")));

        var rejected = Request(h, user: "u1");
        Assert.Equal(429, await Send(h, rejected));
        Assert.Equal(3, h.NextCalls);

        Assert.Equal("10", rejected.Response.Headers.RetryAfter.ToString());
        Assert.Equal("Retry-After", rejected.Response.Headers.AccessControlExposeHeaders.ToString());
        Assert.Equal("application/json", rejected.Response.ContentType);

        var body = Body(rejected);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(10, body.GetProperty("retry_after_seconds").GetInt32());
        Assert.Contains("10 seconds", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WindowOmitted_RetryAfterIsTheDefault60()
    {
        var h = Create(Endpoint(1));
        await Send(h, Request(h, user: "u1"));

        var rejected = Request(h, user: "u1");
        Assert.Equal(429, await Send(h, rejected));
        Assert.Equal("60", rejected.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task CustomMessage_WithPlaceholder()
    {
        var h = Create(Endpoint(1, 30, ("queries:ep:rate_limit:message", "Slow down, retry in {{retry_after_seconds}}s")));
        await Send(h, Request(h, user: "u1"));

        var rejected = Request(h, user: "u1");
        await Send(h, rejected);

        Assert.Equal("Slow down, retry in 30s", Body(rejected).GetProperty("message").GetString());
    }

    [Fact]
    public async Task MissingSection_Returns500_WithErrorCode()
    {
        var h = Create(Endpoint(1));
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        Assert.Equal(500, await Send(h, ctx));
        Assert.Equal(0, h.NextCalls);
        Assert.Contains("Step 4b", Body(ctx).GetProperty("message").GetString());
    }

    // ── precedence ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GlobalLimit_AppliesToEndpointWithoutBlock()
    {
        var h = Create(new() { ["rate_limit:max_requests"] = "2", ["queries:ep:route"] = "x" });

        var codes = await Burst(h, 3, () => Request(h, user: "u1"));

        Assert.Equal([200, 200, 429], codes);
    }

    [Fact]
    public async Task EndpointOverridesGlobal()
    {
        var h = Create(new() { ["rate_limit:max_requests"] = "100", ["queries:ep:rate_limit:max_requests"] = "2" });

        var codes = await Burst(h, 3, () => Request(h, user: "u1"));

        Assert.Equal([200, 200, 429], codes);
    }

    [Fact]
    public async Task EndpointEnabledFalse_OptsOutOfGlobal()
    {
        var h = Create(new() { ["rate_limit:max_requests"] = "1", ["queries:ep:rate_limit:enabled"] = "false" });

        var codes = await Burst(h, 5, () => Request(h, user: "u1"));

        Assert.All(codes, c => Assert.Equal(200, c));
    }

    [Fact]
    public async Task GlobalEnabledFalse_KillSwitch_ThenEndpointCanReEnableItself()
    {
        var off = Create(new()
        {
            ["rate_limit:enabled"] = "false",
            ["rate_limit:max_requests"] = "1",
            ["queries:ep:rate_limit:max_requests"] = "1",
        });
        Assert.All(await Burst(off, 3, () => Request(off, user: "u1")), c => Assert.Equal(200, c));

        var on = Create(new()
        {
            ["rate_limit:enabled"] = "false",
            ["rate_limit:max_requests"] = "1",
            ["queries:ep:rate_limit:enabled"] = "true",
            ["queries:ep:rate_limit:max_requests"] = "1",
        });
        Assert.Equal([200, 429], await Burst(on, 2, () => Request(on, user: "u1")));
    }

    // ── who shares an allowance ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeparateUsers_SeparateAllowances()
    {
        var h = Create(Endpoint(1));

        Assert.Equal(200, await Send(h, Request(h, user: "u1")));
        Assert.Equal(200, await Send(h, Request(h, user: "u2")));
        Assert.Equal(429, await Send(h, Request(h, user: "u1")));
        Assert.Equal(429, await Send(h, Request(h, user: "u2")));
    }

    [Fact]
    public async Task SeparateEndpoints_SeparateAllowances()
    {
        var h = Create(new()
        {
            ["queries:ep:rate_limit:max_requests"] = "1",
            ["queries:other:rate_limit:max_requests"] = "1",
        });

        Assert.Equal(200, await Send(h, Request(h, "ep", user: "u1")));
        Assert.Equal(200, await Send(h, Request(h, "other", user: "u1")));
        Assert.Equal(429, await Send(h, Request(h, "ep", user: "u1")));
    }

    [Fact]
    public async Task PerEndpoint_EveryoneShares()
    {
        var h = Create(Endpoint(2, null, ("queries:ep:rate_limit:per", "endpoint")));

        Assert.Equal(200, await Send(h, Request(h, user: "u1", ip: "10.0.0.1")));
        Assert.Equal(200, await Send(h, Request(h, user: "u2", ip: "10.0.0.2")));
        Assert.Equal(429, await Send(h, Request(h, user: "u3", ip: "10.0.0.3")));
    }

    [Fact]
    public async Task PerIp_IgnoresTheUser()
    {
        var h = Create(Endpoint(1, null, ("queries:ep:rate_limit:per", "ip")));

        Assert.Equal(200, await Send(h, Request(h, user: "u1", ip: "10.0.0.1")));
        Assert.Equal(429, await Send(h, Request(h, user: "u2", ip: "10.0.0.1")));   // different user, same IP: shared
        Assert.Equal(200, await Send(h, Request(h, user: "u1", ip: "10.0.0.2")));   // same user, other IP: separate
    }

    [Fact]
    public async Task AnonymousEndpoint_KeyedOnIp()
    {
        var h = Create(Endpoint(1));

        Assert.Equal(200, await Send(h, Request(h, ip: "10.0.0.1")));
        Assert.Equal(429, await Send(h, Request(h, ip: "10.0.0.1")));
        Assert.Equal(200, await Send(h, Request(h, ip: "10.0.0.2")));
    }

    [Fact]
    public async Task AuthenticatedEndpoint_WithoutIdentityClaims_DoesNotFallBackToIp_AndLogsErrorOnce()
    {
        // Behind a proxy every user shares one IP; falling back would merge them silently.
        var h = Create(Endpoint(1));
        DefaultHttpContext NoIdentity(string ip) => Request(h, ip: ip, setup: c => c.Items["user_claims"] = new Dictionary<string, object>());

        Assert.Equal(200, await Send(h, NoIdentity("10.0.0.1")));
        Assert.Equal(429, await Send(h, NoIdentity("10.0.0.2")));   // shared "unknown user" bucket, not a per-IP one
        Assert.Equal(429, await Send(h, NoIdentity("10.0.0.3")));
        Assert.Equal(1, h.Log.Count(LogLevel.Error));                // visible, but not per request
    }

    [Fact]
    public async Task NotSupportedSubject_FallsBackToEmail()
    {
        var h = Create(Endpoint(1));
        DefaultHttpContext Req(string email) => Request(h, setup: c => c.Items["user_claims"] = new Dictionary<string, object>
        {
            ["user_id"] = "Not supported",
            ["email"] = email,
        });

        Assert.Equal(200, await Send(h, Req("a@x")));
        Assert.Equal(200, await Send(h, Req("b@x")));
        Assert.Equal(429, await Send(h, Req("a@x")));
    }

    [Fact]
    public async Task KeyGatedEndpoint_KeyedOnTheRecordedHash()
    {
        var h = Create(Endpoint(1, null, ("queries:ep:api_keys_collections", "internal")));
        DefaultHttpContext Req(string hash, string ip) => Request(h, ip: ip, setup: c => c.Items["api_key_hash"] = hash);

        Assert.Equal(200, await Send(h, Req("aaaa", "10.0.0.1")));
        Assert.Equal(429, await Send(h, Req("aaaa", "10.0.0.2")));   // same key from another address: same allowance
        Assert.Equal(200, await Send(h, Req("bbbb", "10.0.0.1")));   // another key: its own allowance
    }

    [Fact]
    public async Task ApiKeysCollectionsWithNoNames_IsNotKeyGated_SameRuleAsStep3()
    {
        // Step3 splits on commas and passes through when nothing is left; this step must agree.
        var h = Create(Endpoint(1, null, ("queries:ep:api_keys_collections", " , ")));

        Assert.Equal(200, await Send(h, Request(h, ip: "10.0.0.1")));
        Assert.Equal(200, await Send(h, Request(h, ip: "10.0.0.2")));   // keyed on IP, not on a shared "unknown key" bucket
        Assert.Equal(0, h.Log.Count(LogLevel.Error));
    }

    [Fact]
    public async Task ClientIpHeader_TrustsRightmostEntry_SoALeftSpoofDoesNotMintIdentities()
    {
        var h = Create(new()
        {
            ["rate_limit:max_requests"] = "1",
            ["rate_limit:client_ip_header"] = "X-Forwarded-For",
            ["queries:ep:route"] = "x",
        });
        var i = 0;
        DefaultHttpContext Spoofed(string connection, string rightmost) =>
            Request(h, ip: connection, setup: c => c.Request.Headers["X-Forwarded-For"] = $"9.9.9.{++i}, {rightmost}");

        Assert.Equal(200, await Send(h, Spoofed("10.0.0.1", "203.0.113.7")));
        Assert.Equal(429, await Send(h, Spoofed("10.0.0.2", "203.0.113.7")));   // different socket, different left entry, same trusted entry: shared
        Assert.Equal(200, await Send(h, Spoofed("10.0.0.1", "203.0.113.8")));   // different trusted entry: its own allowance
        Assert.Equal(1, h.Log.Count(LogLevel.Warning));                          // "the engine now trusts this header", once
    }

    // ── robustness ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnparseableConfig_IsTreatedAsNotSet_NoLimit_NeverThrows()
    {
        var h = Create(new()
        {
            ["rate_limit:max_requests"] = "abc",
            ["queries:ep:rate_limit:max_requests"] = "-1",
            ["queries:ep:rate_limit:window_seconds"] = "soon",
            ["queries:ep:rate_limit:enabled"] = "maybe",
        });

        var codes = await Burst(h, 5, () => Request(h, user: "u1"));

        Assert.All(codes, c => Assert.Equal(200, c));
        Assert.Equal(0, h.Log.Count(LogLevel.Error));
        Assert.True(h.Log.Count(LogLevel.Warning) >= 4);   // every typo reported, once each
        Assert.True(h.Log.Count(LogLevel.Warning) <= 6);
    }

    private sealed class ThrowingClaim
    {
        public override string ToString() => throw new InvalidOperationException("claim exploded");
    }

    [Fact]
    public async Task FaultInsideTheDecision_FailsOpen_LogsErrorOnce()
    {
        var h = Create(Endpoint(1));
        // Pick() calls ToString() on the claim value; this one throws, inside the try.
        DefaultHttpContext Req() => Request(h, setup: c => c.Items["user_claims"] = new Dictionary<string, object> { ["user_id"] = new ThrowingClaim() });

        var codes = await Burst(h, 3, Req);

        Assert.All(codes, c => Assert.Equal(200, c));
        Assert.Equal(3, h.NextCalls);
        Assert.Equal(1, h.Log.Count(LogLevel.Error));
        Assert.IsType<InvalidOperationException>(h.Log.Entries.Single(e => e.Level == LogLevel.Error).Exception);
    }

    [Fact]
    public async Task ChangedLimit_StartsAFreshAllowance()
    {
        var h = Create(Endpoint(1));
        await Send(h, Request(h, user: "u1"));
        Assert.Equal(429, await Send(h, Request(h, user: "u1")));

        h.Config["queries:ep:rate_limit:max_requests"] = "3";   // hot reload

        Assert.Equal(200, await Send(h, Request(h, user: "u1")));
    }

    [Fact]
    public async Task RemovedBlock_StopsLimitingImmediately_NoWarning()
    {
        var h = Create(Endpoint(1));
        await Send(h, Request(h, user: "u1"));
        Assert.Equal(429, await Send(h, Request(h, user: "u1")));

        h.Provider.Remove("queries:ep:rate_limit:max_requests");   // the block is gone, not emptied

        Assert.Equal(200, await Send(h, Request(h, user: "u1")));
        Assert.Equal(0, h.Log.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task EmptiedBlock_StopsLimiting_WithWarning()
    {
        var h = Create(Endpoint(1));
        await Send(h, Request(h, user: "u1"));
        Assert.Equal(429, await Send(h, Request(h, user: "u1")));

        h.Config["queries:ep:rate_limit:max_requests"] = null;    // block still exists, value gone

        Assert.Equal(200, await Send(h, Request(h, user: "u1")));
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("NOT rate limited"));
    }

    [Fact]
    public async Task RejectionSummary_NamesTheEndpointAndCallerKind_NotTheUserOrUrl()
    {
        var h = Create(Endpoint(1));
        await Send(h, Request(h, user: "secret-user-id"));
        await Send(h, Request(h, user: "secret-user-id"));   // 429: first rejection writes the summary

        var summary = h.Log.Entries.Single(e => e.Level == LogLevel.Information);
        Assert.Contains("queries:ep", summary.Message);
        Assert.Contains("caller kind user", summary.Message);
        Assert.DoesNotContain("secret-user-id", summary.Message);
    }

    [Fact]
    public async Task ConcurrentBurst_AdmitsExactlyMax()
    {
        var h = Create(Endpoint(25, 60));
        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => Send(h, Request(h, user: "u1")))));

        Assert.Equal(25, results.Count(c => c == 200));
        Assert.Equal(175, results.Count(c => c == 429));
        Assert.Equal(1, h.Log.Count(LogLevel.Information));   // one summary, not 175 lines
    }
}
