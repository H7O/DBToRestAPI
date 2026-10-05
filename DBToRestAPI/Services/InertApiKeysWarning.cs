namespace DBToRestAPI.Services;

/// <summary>
/// Warns about a route that looks protected by API keys but is not: it declares <c>&lt;api_keys&gt;</c>,
/// or an <c>&lt;api_keys_collections&gt;</c> that names no collection (a blank value, or keys laid out as
/// in api_keys.xml), and names no collection as Step3ApiKeysCheck reads it. A self-closing
/// <c>&lt;api_keys_collections/&gt;</c> leaves no trace in the configuration, so it can't be reported.
/// Only a comma-separated list
/// in <c>api_keys_collections</c> protects a route and nothing reads a route's <c>api_keys</c>, so such a
/// route answers callers who send no key.
/// </summary>
/// <remarks>
/// The route resolvers call <see cref="Check"/> each time they load their routes, which is at start-up
/// (Step1ServiceTypeChecks takes both when the pipeline is built) and on every configuration reload.
/// A route is reported once, and again only if it is fixed and the mistake comes back later, so a reload
/// that fires twice for one save does not repeat the warning.
/// </remarks>
public sealed class InertApiKeysWarning(ILogger? logger)
{
    private HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public void Check(IEnumerable<(string Route, IConfigurationSection Section)> routes)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (route, section) in routes)
        {
            if (NamesAKeyCollection(section)) continue;

            string? problem = section.GetSection("api_keys").Exists()
                ? "declares <api_keys>, which does not protect a route"
                : section.GetSection("api_keys_collections").Exists()
                    ? "has an <api_keys_collections> that names no collection"
                    : null;
            if (problem == null) continue;

            found.Add(section.Path);
            if (!_reported.Contains(section.Path))
                logger?.LogWarning(
                    "Route `{Route}` (`{Path}`) {Problem}, so it answers callers without an API key. List the key collections in <api_keys_collections>, for example <api_keys_collections>vendors,internal</api_keys_collections>.",
                    route, section.Path, problem);
        }
        _reported = found;
    }

    // The same reading Step3ApiKeysCheck uses to decide whether a route needs a key.
    private static bool NamesAKeyCollection(IConfigurationSection route)
        => route.GetValue<string>("api_keys_collections")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length > 0;
}
