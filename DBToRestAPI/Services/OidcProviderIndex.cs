using Com.H.Threading;
using Microsoft.Extensions.Primitives;

namespace DBToRestAPI.Services;

/// <summary>
/// Builds and maintains a small, in-memory index of the OIDC providers configured under
/// <c>authorize:providers</c> (auth_providers.xml), so an endpoint that allows MORE THAN ONE
/// provider can resolve an incoming token to exactly one provider before validation.
///
/// <para>
/// This mirrors the resolver pattern used elsewhere in the codebase
/// (see <see cref="QueryRouteResolver"/> / <see cref="RouteConfigResolver"/>):
/// an immutable snapshot swapped atomically, an <see cref="AtomicGate"/> guarding reloads,
/// and a <see cref="ChangeToken"/> subscription that rebuilds the snapshot on hot-reload.
/// </para>
///
/// <para>
/// The index is a <b>routing</b> helper only — it never decides trust. After it selects a
/// provider, <c>Step4JwtAuthorization</c> still performs the full cryptographic validation
/// (signature against the provider's JWKS, issuer, audience, lifetime). See
/// MULTI_PROVIDER_OIDC.md for the design and security rationale.
/// </para>
/// </summary>
public sealed class OidcProviderIndex
{
    private readonly IEncryptedConfiguration _configuration;
    private readonly AtomicGate _reloadingGate = new();

    // Immutable snapshot; reference-swapped on reload (reference assignment is atomic).
    private OidcProviderResolution.ProviderSnapshot _snapshot = OidcProviderResolution.EmptySnapshot;

    public OidcProviderIndex(IEncryptedConfiguration configuration)
    {
        _configuration = configuration;
        Load(); // initial load
        ChangeToken.OnChange(
            () => _configuration.GetSection("authorize:providers").GetReloadToken(),
            Load);
    }

    private void Load()
    {
        try
        {
            if (!_reloadingGate.TryOpen()) return;
            _snapshot = OidcProviderResolution.BuildSnapshot(_configuration.GetSection("authorize:providers"));
        }
        finally
        {
            _reloadingGate.TryClose();
        }
    }

    /// <summary>All provider names currently configured under <c>authorize:providers</c>.</summary>
    public IReadOnlyCollection<string> ProviderNames => _snapshot.ProviderNames;

    /// <summary>
    /// Expands an endpoint's raw <c>&lt;provider&gt;</c> value into the de-duplicated list of
    /// allowed provider names. A single name yields a one-element list (today's behavior);
    /// <c>*</c> expands to every configured provider; an empty/whitespace value yields an empty list
    /// (the inline / no-provider authorization mode).
    /// </summary>
    public List<string> ExpandAllowedProviders(string? providerRaw)
        => OidcProviderResolution.ExpandAllowedProviders(providerRaw, _snapshot);

    /// <summary>
    /// Resolves an incoming token to exactly one allowed provider using the token's (unvalidated)
    /// issuer, disambiguating by the token's (unvalidated) audience when several allowed providers
    /// share the same issuer. Returns <c>null</c> when the issuer matches no allowed provider, or
    /// when it matches several and the audience cannot single one out.
    /// </summary>
    public string? ResolveProviderByIssuer(
        string? tokenIssuer,
        IEnumerable<string>? tokenAudiences,
        IReadOnlyCollection<string> allowedProviders)
        => OidcProviderResolution.ResolveProviderByIssuer(tokenIssuer, tokenAudiences, allowedProviders, _snapshot);
}

/// <summary>
/// Pure, side-effect-free building and resolution logic for <see cref="OidcProviderIndex"/>,
/// factored out so it can be unit-tested without a running host.
/// </summary>
public static class OidcProviderResolution
{
    /// <summary>
    /// Immutable view of the configured providers used for routing decisions.
    /// </summary>
    /// <param name="ProviderNames">Case-insensitive set of configured provider names.</param>
    /// <param name="IssuerToNames">
    /// Exact expected-issuer (<c>&lt;issuer&gt; ?? &lt;authority&gt;</c>, the same value validation
    /// trusts as <c>ValidIssuer</c>) → provider names. A list because two providers can legitimately
    /// share an issuer (e.g. two app registrations in one tenant).
    /// </param>
    /// <param name="NameToAudience">Provider name → configured <c>&lt;audience&gt;</c> (when present).</param>
    public sealed record ProviderSnapshot(
        IReadOnlyCollection<string> ProviderNames,
        IReadOnlyDictionary<string, List<string>> IssuerToNames,
        IReadOnlyDictionary<string, string> NameToAudience);

    public static readonly ProviderSnapshot EmptySnapshot = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, List<string>>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Builds a <see cref="ProviderSnapshot"/> from the <c>authorize:providers</c> configuration section.
    /// </summary>
    public static ProviderSnapshot BuildSnapshot(IConfigurationSection providersSection)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Issuer comparison is ordinal/exact — it must agree byte-for-byte with what token
        // validation enforces as ValidIssuer, otherwise routing could pick a provider whose
        // validation then rejects the token.
        var issuerToNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nameToAudience = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (providersSection is { } && providersSection.Exists())
        {
            foreach (var provider in providersSection.GetChildren())
            {
                var name = provider.Key;
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                    continue;

                // Expected issuer = explicit <issuer> if set, else <authority>. This is the SAME
                // fallback Step4JwtAuthorization uses for ValidIssuer, so routing never disagrees
                // with validation. (Discovery-derived issuers are a documented future enhancement.)
                var expectedIssuer = provider.GetValue<string>("issuer")
                                     ?? provider.GetValue<string>("authority");
                if (!string.IsNullOrWhiteSpace(expectedIssuer))
                {
                    if (!issuerToNames.TryGetValue(expectedIssuer, out var list))
                        issuerToNames[expectedIssuer] = list = new List<string>();
                    list.Add(name);
                }

                var audience = provider.GetValue<string>("audience");
                if (!string.IsNullOrWhiteSpace(audience))
                    nameToAudience[name] = audience;
            }
        }

        return new ProviderSnapshot(names, issuerToNames, nameToAudience);
    }

    /// <summary>See <see cref="OidcProviderIndex.ExpandAllowedProviders"/>.</summary>
    public static List<string> ExpandAllowedProviders(string? providerRaw, ProviderSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(providerRaw))
            return new List<string>();

        var tokens = providerRaw.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // "*" anywhere in the list means: accept any provider configured in auth_providers.xml.
        if (Array.Exists(tokens, t => t == "*"))
            return snapshot.ProviderNames.ToList();

        // De-duplicate, case-insensitively, preserving the configured order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(tokens.Length);
        foreach (var token in tokens)
            if (seen.Add(token))
                result.Add(token);
        return result;
    }

    /// <summary>See <see cref="OidcProviderIndex.ResolveProviderByIssuer"/>.</summary>
    public static string? ResolveProviderByIssuer(
        string? tokenIssuer,
        IEnumerable<string>? tokenAudiences,
        IReadOnlyCollection<string> allowedProviders,
        ProviderSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(tokenIssuer)
            || allowedProviders is null || allowedProviders.Count == 0
            || !snapshot.IssuerToNames.TryGetValue(tokenIssuer, out var issuerMatches))
            return null;

        var allowed = new HashSet<string>(allowedProviders, StringComparer.OrdinalIgnoreCase);
        var candidates = issuerMatches.Where(allowed.Contains).ToList();

        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        // Shared issuer: disambiguate by matching the token's (unvalidated) audience against each
        // candidate's configured <audience>. Validation re-checks the audience, so a wrong guess
        // here can only cause a rejection, never a wrongful acceptance.
        var audiences = tokenAudiences is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(tokenAudiences, StringComparer.Ordinal);

        var audienceMatches = candidates
            .Where(name => snapshot.NameToAudience.TryGetValue(name, out var aud) && audiences.Contains(aud))
            .ToList();

        // Exactly one candidate's audience matches → unambiguous. Otherwise the caller should
        // fall back to requiring the explicit provider hint header.
        return audienceMatches.Count == 1 ? audienceMatches[0] : null;
    }
}
