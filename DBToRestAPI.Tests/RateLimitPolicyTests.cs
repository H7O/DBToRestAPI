using System.Net;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Tests;

/// <summary>
/// Pins the <c>&lt;rate_limit&gt;</c> resolution rules (endpoint → global → default, one tag at a
/// time), the defensive parsing that keeps a config typo from becoming a 500, and the caller
/// identity rules, including the client-IP header handling that must never trust the left-most
/// entry of an appended header.
/// </summary>
public class RateLimitPolicyTests
{
    private static IConfigurationRoot Build(Dictionary<string, string?> data)
        => new ConfigurationBuilder().AddInMemoryCollection(data).Build();

    private static (RateLimitPolicy? policy, List<string> warnings) Resolve(Dictionary<string, string?> data, string endpoint = "ep")
    {
        var root = Build(data);
        var resolver = new RateLimitPolicyResolver(root);
        var warnings = new List<string>();
        var policy = resolver.Resolve(root.GetSection($"queries:{endpoint}"), warnings);
        return (policy, warnings);
    }

    // ── resolution table ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoBlockAnywhere_NoLimit_NoWarning()
    {
        var (policy, warnings) = Resolve(new() { ["queries:ep:route"] = "x" });

        Assert.Null(policy);
        Assert.Empty(warnings);
    }

    [Fact]
    public void GlobalOnly_AppliesToEndpoint()
    {
        var (policy, _) = Resolve(new()
        {
            ["rate_limit:max_requests"] = "120",
            ["rate_limit:window_seconds"] = "60",
            ["queries:ep:route"] = "x",
        });

        Assert.NotNull(policy);
        Assert.Equal(120, policy.MaxRequests);
        Assert.Equal(60, policy.WindowSeconds);
        Assert.Equal(RateLimitPer.Caller, policy.Per);
    }

    [Fact]
    public void EndpointMaxRequestsOnly_InheritsGlobalWindow()
    {
        var (policy, _) = Resolve(new()
        {
            ["rate_limit:max_requests"] = "120",
            ["rate_limit:window_seconds"] = "10",
            ["queries:ep:rate_limit:max_requests"] = "30",
        });

        Assert.Equal(30, policy!.MaxRequests);
        Assert.Equal(10, policy.WindowSeconds);
    }

    [Fact]
    public void EndpointOnly_WindowDefaultsTo60()
    {
        var (policy, warnings) = Resolve(new() { ["queries:ep:rate_limit:max_requests"] = "30" });

        Assert.Equal(30, policy!.MaxRequests);
        Assert.Equal(RateLimitPolicyResolver.DefaultWindowSeconds, policy.WindowSeconds);
        Assert.Empty(warnings);
    }

    [Fact]
    public void EndpointEnabledFalse_OptsOutOfGlobal()
    {
        var (policy, _) = Resolve(new()
        {
            ["rate_limit:max_requests"] = "120",
            ["queries:ep:rate_limit:enabled"] = "false",
        });

        Assert.Null(policy);
    }

    [Fact]
    public void GlobalEnabledFalse_IsKillSwitch_EvenForEndpointWithItsOwnNumbers()
    {
        var (policy, _) = Resolve(new()
        {
            ["rate_limit:enabled"] = "false",
            ["rate_limit:max_requests"] = "120",
            ["queries:ep:rate_limit:max_requests"] = "30",
        });

        Assert.Null(policy);
    }

    [Fact]
    public void EndpointEnabledTrue_OverridesGlobalKillSwitch()
    {
        var (policy, _) = Resolve(new()
        {
            ["rate_limit:enabled"] = "false",
            ["rate_limit:max_requests"] = "120",
            ["queries:ep:rate_limit:enabled"] = "true",
            ["queries:ep:rate_limit:max_requests"] = "30",
        });

        Assert.Equal(30, policy!.MaxRequests);
    }

    [Fact]
    public void BlockPresentButNoMaxRequestsAnywhere_NoLimit_WithWarning()
    {
        // The "looks configured, does nothing" case must not be silent.
        var (policy, warnings) = Resolve(new() { ["queries:ep:rate_limit:window_seconds"] = "10" });

        Assert.Null(policy);
        Assert.Contains(warnings, w => w.Contains("queries:ep") && w.Contains("max_requests") && w.Contains("NOT rate limited"));
    }

    [Fact]
    public void PerAndMessage_ResolveEndpointThenGlobal()
    {
        var (policy, _) = Resolve(new()
        {
            ["rate_limit:max_requests"] = "120",
            ["rate_limit:per"] = "ip",
            ["rate_limit:message"] = "global text",
            ["queries:ep:rate_limit:per"] = " Endpoint ",   // trimmed, case-insensitive
        });

        Assert.Equal(RateLimitPer.Endpoint, policy!.Per);
        Assert.Equal("global text", policy.Message);
    }

    // ── defensive parsing ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1e3")]
    [InlineData("2.5")]
    [InlineData("99999999999")]
    public void BadEndpointMaxRequests_FallsThroughToGlobal(string bad)
    {
        var (policy, warnings) = Resolve(new()
        {
            ["rate_limit:max_requests"] = "120",
            ["queries:ep:rate_limit:max_requests"] = bad,
        });

        Assert.Equal(120, policy!.MaxRequests);
        if (!string.IsNullOrWhiteSpace(bad))
            Assert.Contains(warnings, w => w.Contains("queries:ep:rate_limit:max_requests"));
        else
            Assert.Empty(warnings); // empty is simply "not set", not a typo
    }

    [Fact]
    public void BadGlobalAndNoEndpoint_NoLimit_NeverThrows()
    {
        var (policy, warnings) = Resolve(new()
        {
            ["rate_limit:max_requests"] = "lots",
            ["rate_limit:window_seconds"] = "a minute",
            ["rate_limit:enabled"] = "yes please",
            ["rate_limit:per"] = "user",
        });

        Assert.Null(policy);
        Assert.Equal(4, warnings.Count(w => w.Contains("treated as not set")));   // every typo reported at once
        Assert.Contains(warnings, w => w.Contains("NOT rate limited"));
    }

    [Fact]
    public void BadEnabledOnEndpoint_IsNotSet_SoGlobalStillApplies()
    {
        var (policy, warnings) = Resolve(new()
        {
            ["rate_limit:max_requests"] = "120",
            ["queries:ep:rate_limit:enabled"] = "fasle",
        });

        Assert.NotNull(policy); // the typo did not switch anything off ...
        Assert.Contains(warnings, w => w.Contains("queries:ep:rate_limit:enabled")); // ... and it is reported
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("No", false)]
    [InlineData("off", false)]
    public void TryParseBool_AcceptsCommonSpellings(string raw, bool expected)
    {
        Assert.True(RateLimitPolicyResolver.TryParseBool(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("fasle")]
    [InlineData("")]
    [InlineData("2")]
    public void TryParseBool_RejectsEverythingElse(string raw)
        => Assert.False(RateLimitPolicyResolver.TryParseBool(raw, out _));

    [Theory]
    [InlineData(" 30 ", 30)]
    [InlineData("1", 1)]
    public void TryParsePositiveInt_TrimsAndAccepts(string raw, int expected)
    {
        Assert.True(RateLimitPolicyResolver.TryParsePositiveInt(raw, int.MaxValue, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryParsePositiveInt_HonoursUpperBound()
        => Assert.False(RateLimitPolicyResolver.TryParsePositiveInt("100000", RateLimitPolicyResolver.MaxWindowSeconds, out _));

    [Fact]
    public void DuplicateRateLimitBlocks_UseFirst_WithWarning()
    {
        // The XML provider indexes two <rate_limit> siblings as rate_limit:0 and rate_limit:1.
        var (policy, warnings) = Resolve(new()
        {
            ["queries:ep:rate_limit:0:max_requests"] = "5",
            ["queries:ep:rate_limit:1:max_requests"] = "50",
        });

        Assert.Equal(5, policy!.MaxRequests);
        Assert.Contains(warnings, w => w.Contains("more than one"));
    }

    [Fact]
    public void Segments_NeverBelowOne_NeverAboveTen()
    {
        // The limiter factory reads these off the partition key, so that is what is pinned.
        Assert.Equal(1, new RateLimitPartitionKey("ep", "*", 1, 1).SegmentsPerWindow);
        Assert.Equal(7, new RateLimitPartitionKey("ep", "*", 1, 7).SegmentsPerWindow);
        Assert.Equal(10, new RateLimitPartitionKey("ep", "*", 1, 3600).SegmentsPerWindow);
        Assert.Equal(TimeSpan.FromSeconds(3600), new RateLimitPartitionKey("ep", "*", 1, 3600).Window);
    }

    [Fact]
    public void PartitionKey_EqualityIgnoresComputedMembers_AndSeesLimits()
    {
        Assert.Equal(new RateLimitPartitionKey("ep", "u", 5, 60), new RateLimitPartitionKey("ep", "u", 5, 60));
        Assert.NotEqual(new RateLimitPartitionKey("ep", "u", 5, 60), new RateLimitPartitionKey("ep", "u", 6, 60));
    }

    // ── client-IP settings ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClientIpSettings_DefaultOff()
    {
        var root = Build(new() { ["rate_limit:max_requests"] = "1" });
        var settings = new RateLimitPolicyResolver(root).ResolveClientIpSettings(new List<string>());

        Assert.Null(settings.ClientIpHeader);
    }

    [Fact]
    public void ClientIpSettings_HeaderWithDefaultHops_AndBadHopsWarns()
    {
        var root = Build(new()
        {
            ["rate_limit:client_ip_header"] = "X-Azure-ClientIP",
            ["rate_limit:client_ip_header_trusted_hops"] = "two",
        });
        var warnings = new List<string>();
        var settings = new RateLimitPolicyResolver(root).ResolveClientIpSettings(warnings);

        Assert.Equal("X-Azure-ClientIP", settings.ClientIpHeader);
        Assert.Equal(1, settings.TrustedHops);
        Assert.Contains(warnings, w => w.Contains("client_ip_header_trusted_hops"));
    }

    // ── caller identity ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void FromClaims_PrefersUserId_ThenOid_ThenLongOid_ThenHashedEmail()
    {
        Assert.Equal("user:abc", RateLimitCallerIdentity.FromClaims(new Dictionary<string, object> { ["user_id"] = "abc", ["email"] = "a@b" }));
        Assert.Equal("user:oid1", RateLimitCallerIdentity.FromClaims(new Dictionary<string, object> { ["user_id"] = "Not supported", ["oid"] = "oid1", ["email"] = "a@b" }));
        Assert.Equal("user:oid2", RateLimitCallerIdentity.FromClaims(new Dictionary<string, object> { ["user_id"] = "Not supported", ["http://schemas.microsoft.com/identity/claims/objectidentifier"] = "oid2", ["email"] = "a@b" }));

        var fromEmail = RateLimitCallerIdentity.FromClaims(new Dictionary<string, object> { ["user_id"] = "  ", ["email"] = "A@b" });
        Assert.StartsWith("user:email#", fromEmail);
        Assert.DoesNotContain("@", fromEmail);                                                     // never the address itself
        Assert.Equal(fromEmail, RateLimitCallerIdentity.FromClaims(new Dictionary<string, object> { ["email"] = "a@B" })); // stable, case-insensitive

        Assert.Null(RateLimitCallerIdentity.FromClaims(new Dictionary<string, object> { ["name"] = "no identity here" }));
        Assert.Null(RateLimitCallerIdentity.FromClaims("not a dictionary"));
        Assert.Null(RateLimitCallerIdentity.FromClaims(null));
    }

    [Fact]
    public void FromClaims_PrefixesIssuer_ElseProvider_SoTwoProvidersCannotShareASubject()
    {
        Assert.Equal("user:google:abc", RateLimitCallerIdentity.FromClaims(new Dictionary<string, object> { ["user_id"] = "abc", ["auth_provider"] = "google" }));
        // The issuer is a property of the validated token; a client-chosen provider hint is not.
        Assert.Equal("user:https://issuer.example/:abc", RateLimitCallerIdentity.FromClaims(new Dictionary<string, object>
        {
            ["user_id"] = "abc",
            ["iss"] = "https://issuer.example/",
            ["auth_provider"] = "google",
        }));
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3.4:5000", "1.2.3.4")]
    [InlineData("::1", "::1")]
    [InlineData("[::1]:5000", "::1")]
    [InlineData(" 2001:db8::1 ", "2001:db8::1")]
    public void TryParseAddress_AcceptsAddressesWithAndWithoutPorts(string raw, string expected)
    {
        Assert.True(RateLimitCallerIdentity.TryParseAddress(raw, out var ip));
        Assert.Equal(expected, ip.ToString());
    }

    [Theory]
    [InlineData("evil")]
    [InlineData("")]
    [InlineData("1.2.3.4.5")]
    [InlineData("unknown")]
    public void TryParseAddress_RejectsGarbage(string raw)
        => Assert.False(RateLimitCallerIdentity.TryParseAddress(raw, out _));

    [Fact]
    public void Normalize_MapsIPv4MappedIPv6ToIPv4()
        => Assert.Equal("1.2.3.4", RateLimitCallerIdentity.Normalize(IPAddress.Parse("::ffff:1.2.3.4")));

    private static DefaultHttpContext ContextWith(string? remote, params (string name, string value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        foreach (var (name, value) in headers)
            ctx.Request.Headers.Append(name, value);
        return ctx;
    }

    [Fact]
    public void ClientIp_NoHeaderConfigured_UsesConnection_OrUnknown()
    {
        Assert.Equal("10.0.0.1", RateLimitCallerIdentity.ClientIp(ContextWith("10.0.0.1"), RateLimitClientIpSettings.None, out var d1));
        Assert.Null(d1);
        Assert.Equal("unknown", RateLimitCallerIdentity.ClientIp(ContextWith(null), RateLimitClientIpSettings.None, out _));
    }

    [Fact]
    public void ClientIp_AppendedHeader_TakesRightmost_NeverTheClientSuppliedLeft()
    {
        // Attacker sends "X-Forwarded-For: 9.9.9.9"; the proxy appends the real socket address.
        var ctx = ContextWith("10.0.0.1", ("X-Forwarded-For", "9.9.9.9, 203.0.113.7"));
        var ip = RateLimitCallerIdentity.ClientIp(ctx, new("X-Forwarded-For", 1), out var degradation);

        Assert.Equal("203.0.113.7", ip);
        Assert.Null(degradation);
    }

    [Fact]
    public void ClientIp_TrustedHopsTwo_TakesSecondFromRight()
    {
        // Front Door appends the client, then App Service's front end appends Front Door.
        var ctx = ContextWith("10.0.0.1", ("X-Forwarded-For", "9.9.9.9, 203.0.113.7, 147.243.1.1"));
        Assert.Equal("203.0.113.7", RateLimitCallerIdentity.ClientIp(ctx, new("X-Forwarded-For", 2), out _));
    }

    [Fact]
    public void ClientIp_MultipleHeaderValues_AreConcatenatedInOrder()
    {
        var ctx = ContextWith("10.0.0.1", ("X-Forwarded-For", "9.9.9.9"), ("X-Forwarded-For", "203.0.113.7"));
        Assert.Equal("203.0.113.7", RateLimitCallerIdentity.ClientIp(ctx, new("X-Forwarded-For", 1), out _));
    }

    [Fact]
    public void ClientIp_HeaderGarbage_FallsBackToConnection_WithDegradation()
    {
        var ctx = ContextWith("10.0.0.1", ("X-Azure-ClientIP", "not-an-ip"));
        var ip = RateLimitCallerIdentity.ClientIp(ctx, new("X-Azure-ClientIP", 1), out var degradation);

        Assert.Equal("10.0.0.1", ip);
        Assert.NotNull(degradation);
        Assert.Contains("not an IP address", degradation);
        Assert.DoesNotContain("not-an-ip", degradation); // header values are attacker-controlled; never echo them into logs
    }

    [Fact]
    public void ClientIp_TooFewEntriesForHops_FallsBackToConnection()
    {
        var ctx = ContextWith("10.0.0.1", ("X-Forwarded-For", "203.0.113.7"));
        Assert.Equal("10.0.0.1", RateLimitCallerIdentity.ClientIp(ctx, new("X-Forwarded-For", 3), out var degradation));
        Assert.Contains("fewer entries", degradation);
    }

    [Fact]
    public void ClientIp_ConfiguredHeaderAbsent_FallsBackToConnection_AndSaysSo()
    {
        // A wrong header name, or a request that reached the engine without going through the proxy.
        var ctx = ContextWith("10.0.0.1");
        Assert.Equal("10.0.0.1", RateLimitCallerIdentity.ClientIp(ctx, new("X-Azure-ClientIP", 1), out var degradation));
        Assert.Contains("did not carry it", degradation);
    }

    [Fact]
    public void Resolve_AuthenticatedEndpointWithoutIdentity_SharesBucket_NotIp()
    {
        var ctx = ContextWith("10.0.0.1");
        ctx.Items["user_claims"] = new Dictionary<string, object>();

        var caller = RateLimitCallerIdentity.Resolve(ctx, false, RateLimitClientIpSettings.None, out var degradation);

        Assert.Equal(RateLimitCallerIdentity.UnknownUser, caller);
        Assert.NotNull(degradation);
    }

    [Fact]
    public void Resolve_KeyGated_UsesRecordedHash_NeverTheHeader()
    {
        var ctx = ContextWith("10.0.0.1", ("x-api-key", "the-secret"));
        ctx.Items["api_key_hash"] = "abcdef0123456789";

        Assert.Equal("key:abcdef0123456789", RateLimitCallerIdentity.Resolve(ctx, true, RateLimitClientIpSettings.None, out var d1));
        Assert.Null(d1);

        ctx.Items.Remove("api_key_hash");
        Assert.Equal(RateLimitCallerIdentity.UnknownKey, RateLimitCallerIdentity.Resolve(ctx, true, RateLimitClientIpSettings.None, out var d2));
        Assert.NotNull(d2);
    }

    [Fact]
    public void Resolve_AnonymousEndpoint_IgnoresStrayApiKeyHeader_UsesIp()
    {
        var ctx = ContextWith("10.0.0.1", ("x-api-key", "whatever"));
        Assert.Equal("ip:10.0.0.1", RateLimitCallerIdentity.Resolve(ctx, false, RateLimitClientIpSettings.None, out _));
    }

    [Fact]
    public void IdentityHash_IsShortStableAndOneWay()
    {
        var h = IdentityHash.Short("the-secret");
        Assert.Equal(16, h.Length);
        Assert.Equal(h, IdentityHash.Short("the-secret"));
        Assert.NotEqual(h, IdentityHash.Short("the-secret2"));
        Assert.DoesNotContain("secret", h);
    }
}
