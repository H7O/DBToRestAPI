using DBToRestAPI.Controllers;

namespace DBToRestAPI.Services;

/// <summary>
/// Warns about a <c>response_structure</c> the author should change. The documented values are
/// <c>array</c> and <c>file</c>; leaving the tag out gives the row-count shape. From 1.7.8
/// <c>single</c> and <c>auto</c> are read as that shape (see
/// <see cref="ApiController.ResolveResponseStructure"/>), so the tag should go, and a
/// <c>single</c> query that returns several rows now answers all of them. A value the engine
/// doesn't know makes every request to the route answer 500, unless the route has a
/// <c>count_query</c>, which ignores the tag. A global <c>response_structure</c> under
/// <c>settings</c>, which earlier versions applied to every route without its own tag, is no
/// longer read, so one that is still set is reported too.
/// </summary>
/// <remarks>
/// QueryRouteResolver calls <see cref="Check"/> each time it loads its routes, at start-up and on
/// every configuration reload. As with <see cref="InertApiKeysWarning"/>, a warning is logged once,
/// and again only if it goes away and comes back later.
/// </remarks>
public sealed class ResponseStructureWarning(ILogger? logger)
{
    private enum Problem { None, Single, Auto, Unknown }

    private HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public void Check(IEnumerable<(string Route, IConfigurationSection Section)> routes, IConfiguration root)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var globalValue = root.GetSection("response_structure").Value;
        if (!string.IsNullOrWhiteSpace(globalValue) && IsNew(found, "=" + globalValue))
            logger?.LogWarning(
                "The global <response_structure>{Value}</response_structure> under <settings> is not read from 1.7.8: a route without its own tag answers one row as an object and several rows as an array. Remove it, and set <response_structure>array</response_structure> on the routes that return lists.",
                globalValue);

        foreach (var (route, section) in routes)
        {
            var value = section.GetValue<string>("response_structure");
            var hasCountQuery = !string.IsNullOrWhiteSpace(section.GetValue<string>("count_query"));
            // The key holds what the message depends on, so adding or removing a count_query reports
            // the route again.
            var key = section.Path + "=" + value + "|" + hasCountQuery;

            switch (Classify(value))
            {
                case Problem.Single or Problem.Auto when hasCountQuery:
                    if (IsNew(found, key))
                        logger?.LogWarning(
                            "Route `{Route}` (`{Path}`) has <response_structure>{Value}</response_structure>, which a route with a count_query ignores. Remove the tag.",
                            route, section.Path, value);
                    break;
                case Problem.Single:
                    if (IsNew(found, key))
                        logger?.LogWarning(
                            "Route `{Route}` (`{Path}`) has <response_structure>{Value}</response_structure>, which is read as the default from 1.7.8: one row answers an object, several rows an array of them all (before 1.7.8, only the first row). If the query can return several rows and only the first is wanted, limit it to one (TOP 1, LIMIT 1). Then remove the tag.",
                            route, section.Path, value);
                    break;
                case Problem.Auto:
                    if (IsNew(found, key))
                        logger?.LogWarning(
                            "Route `{Route}` (`{Path}`) has <response_structure>{Value}</response_structure>, which is the default: one row answers an object, several rows an array. Remove the tag.",
                            route, section.Path, value);
                    break;
                case Problem.Unknown when !hasCountQuery:
                    if (IsNew(found, key))
                        logger?.LogWarning(
                            "Route `{Route}` (`{Path}`) has <response_structure>{Value}</response_structure>, which the engine doesn't know, so every request to it answers 500. Use array or file, or remove the tag (one row then answers an object, several rows an array).",
                            route, section.Path, value);
                    break;
            }
        }
        _reported = found;
    }

    // Records the key as still present, and says whether it was not reported at the last load.
    private bool IsNew(HashSet<string> found, string key)
    {
        found.Add(key);
        return !_reported.Contains(key);
    }

    private static Problem Classify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "array", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "file", StringComparison.OrdinalIgnoreCase))
            return Problem.None;
        if (string.Equals(value, "single", StringComparison.OrdinalIgnoreCase))
            return Problem.Single;
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
            return Problem.Auto;
        return Problem.Unknown;
    }
}
