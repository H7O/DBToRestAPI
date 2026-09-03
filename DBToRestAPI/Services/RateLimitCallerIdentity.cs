using System.Net;
using Microsoft.AspNetCore.Http;

namespace DBToRestAPI.Services;

/// <summary>
/// Works out the identity a rate-limit allowance belongs to. Pure functions over
/// <c>HttpContext</c> so the rules can be unit-tested without the pipeline.
/// </summary>
public static class RateLimitCallerIdentity
{
    /// <summary>Shared bucket for callers on an authenticated endpoint whose token carries no usable identity.</summary>
    public const string UnknownUser = "user:unknown";

    /// <summary>Shared bucket for callers on a key-gated endpoint when Step3 did not record the key hash.</summary>
    public const string UnknownKey = "key:unknown";

    /// <summary>Some identity providers emit this literal instead of a subject when the subject claim is disabled.</summary>
    private const string NotSupported = "Not supported";

    /// <summary>The long claim type .NET's inbound claim mapping can turn <c>oid</c> into.</summary>
    private const string ObjectIdentifierClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>
    /// The caller for <see cref="RateLimitPer.Caller"/>, in this order:
    /// <list type="number">
    /// <item>the authenticated user (<c>user_claims</c> from Step4: <c>user_id</c>, else <c>oid</c> in its
    ///       short or long form, else a hash of <c>email</c>), prefixed with the token's issuer when
    ///       present, else the provider name;</item>
    /// <item>the validated API key (<c>api_key_hash</c> from Step3) when the endpoint declares <c>api_keys_collections</c>;</item>
    /// <item>the client IP.</item>
    /// </list>
    /// An authenticated endpoint never falls through to the IP: behind a proxy that would merge
    /// every user into one bucket. It uses <see cref="UnknownUser"/> instead and reports it through
    /// <paramref name="degradation"/> so the operator can see it. A key-gated endpoint with no
    /// recorded hash uses <see cref="UnknownKey"/> the same way.
    /// </summary>
    public static string Resolve(
        HttpContext context,
        bool endpointRequiresApiKey,
        RateLimitClientIpSettings clientIp,
        out string? degradation)
    {
        degradation = null;

        if (context.Items.TryGetValue("user_claims", out var claimsObj))
        {
            var user = FromClaims(claimsObj);
            if (user is not null)
                return user;

            degradation = "the token carries no user_id, oid (short or long form) or email claim; all such callers share one allowance";
            return UnknownUser;
        }

        if (endpointRequiresApiKey)
        {
            if (context.Items.TryGetValue("api_key_hash", out var hashObj)
                && hashObj is string hash
                && !string.IsNullOrWhiteSpace(hash))
                return "key:" + hash;

            degradation = "the endpoint requires an API key but no api_key_hash was recorded; all such callers share one allowance";
            return UnknownKey;
        }

        return "ip:" + ClientIp(context, clientIp, out degradation);
    }

    /// <summary>
    /// The <c>user:</c> identity from Step4's unified claims dictionary, or null. An e-mail address
    /// is hashed before it becomes an identity, so it never reaches a log line; the <c>email#</c>
    /// marker tells the operator the degraded path was taken.
    /// </summary>
    public static string? FromClaims(object? userClaims)
    {
        if (userClaims is not IDictionary<string, object> claims)
            return null;

        var id = Pick(claims, "user_id")
            ?? Pick(claims, "oid")
            ?? Pick(claims, ObjectIdentifierClaim);

        if (id is null)
        {
            var email = Pick(claims, "email");
            if (email is null)
                return null;
            id = "email#" + IdentityHash.Short(email.ToLowerInvariant());
        }

        // The issuer is a property of the validated token; the provider name is whichever
        // configured block validated it, which on a multi-provider endpoint the client may hint.
        var scope = Pick(claims, "iss") ?? Pick(claims, "auth_provider");
        return scope is null ? "user:" + id : $"user:{scope}:{id}";
    }

    /// <summary>
    /// The client address as text. With a <c>client_ip_header</c> configured, the header's entries
    /// (all values, split on commas) are read from the RIGHT: with <c>trusted_hops</c> = 1 the
    /// right-most entry, the address the last trusted proxy saw. The left-most entry of an
    /// appended header such as <c>X-Forwarded-For</c> is whatever the client chose to send and is
    /// never used. A configured header that is absent, has fewer entries than <c>trusted_hops</c>,
    /// or does not hold an IP address falls back to the connection address and is reported through
    /// <paramref name="degradation"/>: each of those means the proxy is not doing what the
    /// configuration says, or the engine is reachable without it.
    /// </summary>
    public static string ClientIp(HttpContext context, RateLimitClientIpSettings settings, out string? degradation)
    {
        degradation = null;

        if (!string.IsNullOrWhiteSpace(settings.ClientIpHeader))
        {
            var header = settings.ClientIpHeader;

            if (context.Request.Headers.TryGetValue(header, out var values) && values.Count > 0)
            {
                var entries = new List<string>();
                foreach (var value in values)
                {
                    if (value is null) continue;
                    foreach (var part in value.Split(','))
                    {
                        var trimmed = part.Trim();
                        if (trimmed.Length > 0)
                            entries.Add(trimmed);
                    }
                }

                var hops = Math.Max(1, settings.TrustedHops);
                var index = entries.Count - hops;

                if (index < 0)
                    degradation = $"header `{header}` has fewer entries than client_ip_header_trusted_hops={hops}; using the connection address instead";
                else if (TryParseAddress(entries[index], out var fromHeader))
                    return Normalize(fromHeader);
                else
                    degradation = $"header `{header}` entry is not an IP address; using the connection address instead";
            }
            else
            {
                degradation = $"header `{header}` is configured as client_ip_header but the request did not carry it (wrong header name, or the engine is reachable without the proxy); using the connection address instead";
            }
        }

        var remote = context.Connection.RemoteIpAddress;
        return remote is null ? "unknown" : Normalize(remote);
    }

    /// <summary>Accepts <c>1.2.3.4</c>, <c>1.2.3.4:5000</c>, <c>::1</c>, <c>[::1]:5000</c>.</summary>
    internal static bool TryParseAddress(string entry, out IPAddress address)
    {
        var s = entry.Trim();

        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            if (end > 0)
                s = s[1..end];
        }
        else if (s.Count(c => c == ':') == 1)
        {
            // exactly one colon: IPv4 with a port
            s = s[..s.IndexOf(':')];
        }

        if (IPAddress.TryParse(s, out var parsed))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    /// <summary>Kestrel reports IPv4 clients on a dual-stack socket as <c>::ffff:1.2.3.4</c>; IIS as <c>1.2.3.4</c>. Same caller, same text.</summary>
    internal static string Normalize(IPAddress address)
        => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    private static string? Pick(IDictionary<string, object> claims, string key)
    {
        if (!claims.TryGetValue(key, out var value) || value is null)
            return null;

        var text = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals(NotSupported, StringComparison.OrdinalIgnoreCase))
            return null;

        return text;
    }
}
