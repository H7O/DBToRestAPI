using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Services;

/// <summary>
/// What a <c>&lt;rate_limit&gt;</c> block resolves to for one endpoint.
/// </summary>
/// <param name="MaxRequests">Permits per window. Always ≥ 1.</param>
/// <param name="WindowSeconds">Window length in seconds. Always ≥ 1.</param>
/// <param name="Per">Who shares an allowance: each caller, each client IP, or everyone on the endpoint.</param>
/// <param name="Message">Custom 429 message, or null for the built-in one.</param>
public sealed record RateLimitPolicy(int MaxRequests, int WindowSeconds, RateLimitPer Per, string? Message);

/// <summary>Value of the <c>&lt;per&gt;</c> tag.</summary>
public enum RateLimitPer
{
    /// <summary>The authenticated user, else the validated API key, else the client IP (default).</summary>
    Caller,
    /// <summary>Always the client IP, even when the caller is authenticated.</summary>
    Ip,
    /// <summary>One shared allowance for every request to the endpoint.</summary>
    Endpoint,
}

/// <summary>
/// Global-only settings that describe the deployment rather than an endpoint.
/// </summary>
/// <param name="ClientIpHeader">Header a trusted reverse proxy fills with the client address, or null to use the connection address.</param>
/// <param name="TrustedHops">How many proxies append to that header. 1 = take the right-most entry.</param>
public sealed record RateLimitClientIpSettings(string? ClientIpHeader, int TrustedHops)
{
    public static readonly RateLimitClientIpSettings None = new(null, 1);
}

/// <summary>
/// Partition key for the shared <c>PartitionedRateLimiter</c>. The limits are part of the key on
/// purpose: a hot-reloaded change starts fresh partitions immediately and the old ones age out,
/// so no limiter ever runs with stale options and nothing needs rebuilding on reload.
/// </summary>
public readonly record struct RateLimitPartitionKey(string EndpointPath, string Caller, int MaxRequests, int WindowSeconds)
{
    /// <summary>
    /// The sliding window is divided into at most 10 segments so a 60 s window slides every 6 s
    /// and a 10 s window every second. Never fewer than one segment (a 1 s window is a fixed window).
    /// Lives on the key because the limiter factory only ever sees the key. Computed properties do
    /// not take part in the record's equality.
    /// </summary>
    public int SegmentsPerWindow => Math.Clamp(WindowSeconds, 1, 10);

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

/// <summary>
/// Resolves the effective <c>&lt;rate_limit&gt;</c> for an endpoint: endpoint value → global value
/// (the <c>&lt;rate_limit&gt;</c> block directly under <c>&lt;settings&gt;</c>) → built-in default,
/// one tag at a time, exactly the way <c>file_management</c> settings resolve in
/// <see cref="ParametersBuilder"/>.
///
/// Every value is read as text and parsed here rather than through <c>GetValue&lt;T&gt;</c>, because
/// the configuration binder throws on a non-numeric value and a throw on this path would turn a
/// typo in settings.xml into a 500 on every request. Anything that does not parse is treated as
/// "not set" at that level and reported through <c>warnings</c>, so the caller can log it once.
/// </summary>
public sealed class RateLimitPolicyResolver(IConfiguration globalConfiguration)
{
    public const string SectionName = "rate_limit";
    public const int DefaultWindowSeconds = 60;
    public const int MaxWindowSeconds = 86_400;

    private readonly IConfiguration _global = globalConfiguration;

    /// <summary>
    /// The effective policy for <paramref name="endpoint"/>, or null when the endpoint is not limited
    /// (no block anywhere, <c>enabled</c> resolves to false, or no valid <c>max_requests</c> resolves).
    /// </summary>
    public RateLimitPolicy? Resolve(IConfigurationSection endpoint, ICollection<string> warnings)
    {
        var endpointPath = endpoint.Path;
        var ep = Block(endpoint.GetSection(SectionName), endpointPath, warnings);
        var gl = Block(_global.GetSection(SectionName), "settings", warnings);

        if (ep is null && gl is null)
            return null;

        // Per-tag resolution: endpoint → global → default. Every tag is read before anything is
        // decided so that all typos are reported together, not one per edit-and-retry.
        var enabled = ReadBool(ep, "enabled", warnings) ?? ReadBool(gl, "enabled", warnings) ?? true;

        var maxRequests = ReadPositiveInt(ep, "max_requests", int.MaxValue, warnings)
            ?? ReadPositiveInt(gl, "max_requests", int.MaxValue, warnings);

        var windowSeconds = ReadPositiveInt(ep, "window_seconds", MaxWindowSeconds, warnings)
            ?? ReadPositiveInt(gl, "window_seconds", MaxWindowSeconds, warnings)
            ?? DefaultWindowSeconds;

        var per = ReadPer(ep, warnings) ?? ReadPer(gl, warnings) ?? RateLimitPer.Caller;

        var message = ReadText(ep, "message") ?? ReadText(gl, "message");

        // A global <enabled>false</enabled> is the kill switch; an endpoint may still switch
        // itself back on with <enabled>true</enabled>.
        if (!enabled)
            return null;

        if (maxRequests is null)
        {
            warnings.Add($"`{endpointPath}`: a <{SectionName}> block is present but no valid <max_requests> resolves at endpoint or global level, so the endpoint is NOT rate limited");
            return null;
        }

        return new RateLimitPolicy(maxRequests.Value, windowSeconds, per, message);
    }

    /// <summary>The global client-IP settings (these have no per-endpoint form).</summary>
    public RateLimitClientIpSettings ResolveClientIpSettings(ICollection<string> warnings)
    {
        var gl = Block(_global.GetSection(SectionName), "settings", warnings);
        if (gl is null)
            return RateLimitClientIpSettings.None;

        var header = ReadText(gl, "client_ip_header");
        if (header is null)
            return RateLimitClientIpSettings.None;

        var hops = ReadPositiveInt(gl, "client_ip_header_trusted_hops", 64, warnings) ?? 1;
        return new RateLimitClientIpSettings(header, hops);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The block to read, or null when absent. Two <c>&lt;rate_limit&gt;</c> siblings are indexed by
    /// the XML provider as <c>rate_limit:0</c> and <c>rate_limit:1</c>; the first is used and the
    /// duplication is reported, because otherwise every value would silently read as "not set".
    /// </summary>
    private static IConfigurationSection? Block(IConfigurationSection section, string owner, ICollection<string> warnings)
    {
        if (!section.Exists())
            return null;

        var first = section.GetSection("0");
        if (first.Exists())
        {
            warnings.Add($"`{owner}`: more than one <{SectionName}> block is defined; only the first is used");
            return first;
        }

        return section;
    }

    private static string? ReadText(IConfigurationSection? block, string key)
    {
        var raw = block?.GetSection(key).Value;
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static int? ReadPositiveInt(IConfigurationSection? block, string key, int max, ICollection<string> warnings)
    {
        if (block is null)
            return null;

        var raw = block.GetSection(key).Value;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (TryParsePositiveInt(raw, max, out var value))
            return value;

        warnings.Add($"`{block.Path}:{key}` = `{Sanitize(raw)}` is not a whole number between 1 and {max}; treated as not set");
        return null;
    }

    private static bool? ReadBool(IConfigurationSection? block, string key, ICollection<string> warnings)
    {
        if (block is null)
            return null;

        var raw = block.GetSection(key).Value;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (TryParseBool(raw, out var value))
            return value;

        warnings.Add($"`{block.Path}:{key}` = `{Sanitize(raw)}` is not true/false; treated as not set");
        return null;
    }

    private static RateLimitPer? ReadPer(IConfigurationSection? block, ICollection<string> warnings)
    {
        if (block is null)
            return null;

        var raw = block.GetSection("per").Value;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (TryParsePer(raw, out var value))
            return value;

        warnings.Add($"`{block.Path}:per` = `{Sanitize(raw)}` is not one of caller/ip/endpoint; treated as not set");
        return null;
    }

    internal static bool TryParsePositiveInt(string raw, int max, out int value)
    {
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= 1 && value <= max)
            return true;

        value = 0;
        return false;
    }

    internal static bool TryParseBool(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true": case "1": case "yes": case "on":
                value = true; return true;
            case "false": case "0": case "no": case "off":
                value = false; return true;
            default:
                value = false; return false;
        }
    }

    internal static bool TryParsePer(string raw, out RateLimitPer value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "caller": value = RateLimitPer.Caller; return true;
            case "ip": value = RateLimitPer.Ip; return true;
            case "endpoint": value = RateLimitPer.Endpoint; return true;
            default: value = RateLimitPer.Caller; return false;
        }
    }

    /// <summary>Config values end up in log lines; keep them to one short printable line.</summary>
    private static string Sanitize(string raw)
    {
        var oneLine = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 40 ? oneLine : oneLine[..40] + "…";
    }
}
