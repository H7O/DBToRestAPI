using DBToRestAPI.Settings;
using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

/// <summary>
/// Resolves database query routes from configuration by matching incoming route paths and HTTP verbs
/// against configured query endpoints, including support for parameterized routes.
/// 
/// This service maintains two collections for optimal performance:
/// - Exact routes: Routes without variables for fast direct lookup
/// - Parameterized routes: Routes with variables (e.g., {id}, {name}) that require pattern matching
/// 
/// The resolver automatically reloads routes when the configuration changes.
/// 
/// Route matching process:
/// 1. Attempts exact match with both route and verb
/// 2. Falls back to exact route match with no verb specified (verb-agnostic)
/// 3. Uses scoring algorithm to find best matching parameterized route
/// 
/// Scoring algorithm prioritizes specificity:
/// - Exact segment matches score higher (10 points) than parameter matches (5 points)
/// - Routes must have matching segment counts to be considered
/// - The route with the highest score wins
/// 
/// Example route configurations:
/// - Exact route: "api/users" with verb "GET" matches only exact path with GET method
/// - Parameterized route: "api/users/{id}" matches "api/users/123", "api/users/abc", etc.
/// - Verb-agnostic route: "api/data" with no verb matches any HTTP method
/// 
/// Reloads run one at a time: a reload that arrives while another runs waits for it, so none
/// is dropped.
/// </summary>
public class QueryRouteResolver
{

    private List<(string NormalizedRoute, HashSet<string> Verbs, string? HostPattern, IConfigurationSection Config)> _exactRoutes = new();
    // The exact routes by their text, each route's entries in the order of _exactRoutes.
    private Dictionary<string, List<(string NormalizedRoute, HashSet<string> Verbs, string? HostPattern, IConfigurationSection Config)>> _exactRoutesByRoute = new(StringComparer.OrdinalIgnoreCase);
    // The routes with variables, each with the route variable pattern its segments are read with.
    private List<(string NormalizedRoute, HashSet<string> Verbs, string? HostPattern, IConfigurationSection Config, Regex VariablePattern)> _routesWithVariables = new();
    private readonly IEncryptedConfiguration _configuration;


    private readonly Lock _reloadLock = new();

    public QueryRouteResolver(IEncryptedConfiguration configuration)
    {
        _configuration = configuration;
        LoadRoutes(); // initial load
        ChangeToken.OnChange(
            () => _configuration.GetSection("queries").GetReloadToken(),
            LoadRoutes);
    }

    // Each route variable pattern, compiled once. Compiling one takes about a millisecond of CPU, and
    // building one per request made it most of a request's cost. The patterns come from the
    // configuration, so there are few of them.
    private static readonly ConcurrentDictionary<string, Regex> _routeVariableRegexes = new(StringComparer.Ordinal);

    internal static Regex RouteVariablesRegex(string? pattern) =>
        string.IsNullOrWhiteSpace(pattern) || pattern == DefaultRegex.DefaultRouteVariablesPattern
            ? DefaultRegex.DefaultRouteVariablesCompiledRegex
            : _routeVariableRegexes.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled));

    /// <summary>
    /// The pattern that finds the variables in an endpoint's route: its own, else the global one,
    /// else (null) the default. A route is matched and its values read with the same one.
    /// </summary>
    private string? RouteVariablePattern(IConfigurationSection endpointSection)
    {
        var pattern = endpointSection.GetValue<string>("route_variable_pattern");
        return string.IsNullOrWhiteSpace(pattern) ? _configuration.GetValue<string>("route_variable_pattern") : pattern;
    }

    // Each configured route's segments, with the name of the variable each holds (null for a segment
    // matched as it is written), per route variable pattern. Read once per route instead of per request.
    private static readonly ConcurrentDictionary<(Regex Pattern, string Route), (string[] Segments, string?[] Variables)> _routeSegments = new();

    private static (string[] Segments, string?[] Variables) RouteSegments(Regex pattern, string route) =>
        _routeSegments.GetOrAdd((pattern, route), key =>
        {
            var segments = key.Route.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var variables = new string?[segments.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                var match = key.Pattern.Match(segments[i]);
                if (match.Success && match.Groups["param"].Success)
                    variables[i] = match.Groups["param"].Value;
            }
            return (segments, variables);
        });

    private static readonly HashSet<string> _allVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"
    };

    private void LoadRoutes()
    {
        _reloadLock.Enter();
        try
        {
            var querySections = _configuration.GetSection("queries");
            if (querySections == null || !querySections.Exists())
            {
                // The last endpoint was removed: none answers any more.
                _exactRoutes = new();
                _exactRoutesByRoute = new(StringComparer.OrdinalIgnoreCase);
                _routesWithVariables = new();
                return;
            }

            var newExactRoutes = new List<(string NormalizedRoute, HashSet<string> Verbs, string? HostPattern, IConfigurationSection Config)>();
            var newRoutesWithVariables = new List<(string NormalizedRoute, HashSet<string> Verbs, string? HostPattern, IConfigurationSection Config, Regex VariablePattern)>();
            var newExactRouteVerbs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var querySection in querySections.GetChildren())
            {
                // Expand duplicate XML tags — .NET's XmlConfigurationProvider indexes
                // same-name sibling elements as numeric sub-children (0, 1, 2, …).
                // ExpandDuplicateXmlSections detects this and yields each sub-child
                // individually; for normal (unique) sections it yields the section itself.
                foreach (var endpointSection in ExpandDuplicateXmlSections(querySection))
                {
                    // For expanded duplicate sections the Key is a numeric index ("0", "1", …),
                    // so fall back to the parent tag name when no explicit route is defined.
                    var route = endpointSection.GetValue<string>("route")
                        ?? (int.TryParse(endpointSection.Key, out _) ? querySection.Key : endpointSection.Key);
                    if (string.IsNullOrWhiteSpace(route)) continue;
                    var normalizedRoute = NormalizeRoute(route);
                    if (string.IsNullOrWhiteSpace(normalizedRoute)) continue;

                    var verbString = endpointSection.GetValue<string>("verb");
                    var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (!string.IsNullOrWhiteSpace(verbString))
                    {
                        var splitVerbs = verbString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var v in splitVerbs) verbs.Add(v);
                        // Ensure OPTIONS is present if specific verbs are defined
                        verbs.Add("OPTIONS");
                    }
                    else
                    {
                        // No verb defined -> implies all verbs
                        foreach (var v in _allVerbs) verbs.Add(v);
                    }

                    var routeParametersRegex = RouteVariablesRegex(RouteVariablePattern(endpointSection));

                    // Read optional host pattern (e.g., "www.example.com" or "*.example.com")
                    var hostPattern = endpointSection.GetValue<string>("host");
                    if (string.IsNullOrWhiteSpace(hostPattern)) hostPattern = null;

                    if (routeParametersRegex.IsMatch(normalizedRoute))
                    {
                        // Store routes with variables separately
                        newRoutesWithVariables.Add((normalizedRoute, verbs, hostPattern, endpointSection, routeParametersRegex));
                    }
                    else
                    {
                        // Store exact routes separately
                        newExactRoutes.Add((normalizedRoute, verbs, hostPattern, endpointSection));

                        if (!newExactRouteVerbs.ContainsKey(normalizedRoute))
                        {
                            newExactRouteVerbs[normalizedRoute] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }

                        foreach (var v in verbs)
                        {
                            newExactRouteVerbs[normalizedRoute].Add(v);
                        }
                    }
                }
            }

            var newExactRoutesByRoute = new Dictionary<string, List<(string NormalizedRoute, HashSet<string> Verbs, string? HostPattern, IConfigurationSection Config)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var exactRoute in newExactRoutes)
            {
                if (!newExactRoutesByRoute.TryGetValue(exactRoute.NormalizedRoute, out var sameRoute))
                    newExactRoutesByRoute[exactRoute.NormalizedRoute] = sameRoute = new();
                sameRoute.Add(exactRoute);
            }

            _exactRoutes = newExactRoutes;
            _exactRoutesByRoute = newExactRoutesByRoute;
            _routesWithVariables = newRoutesWithVariables;
            // _exactRouteVerbs = newExactRouteVerbs;
        }
        finally
        {
            _reloadLock.Exit();
        }
    }

    /// <summary>
    /// Detects when an IConfiguration section represents a group of duplicate XML sibling elements.
    /// .NET's XmlConfigurationProvider indexes same-name siblings with numeric keys (0, 1, 2, …),
    /// so the section itself has no direct route/query values — they live under each numeric child.
    /// For normal (unique) sections, yields the section as-is.
    /// </summary>
    private static IEnumerable<IConfigurationSection> ExpandDuplicateXmlSections(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();

        if (children.Count > 0
            && children.All(c => int.TryParse(c.Key, out _))
            && children.Any(c => c.GetSection("query").Exists()))
        {
            foreach (var child in children)
            {
                yield return child;
            }
        }
        else
        {
            yield return section;
        }
    }


    public IConfigurationSection? ResolveRoute(string urlRoute, string verb, string? requestHost = null)
    {
        // A HEAD request is a GET whose answer has no body: a route that takes GET answers it, unless a
        // route takes HEAD itself.
        return ResolveRouteForVerb(urlRoute, verb, requestHost)
            ?? (string.Equals(verb, "HEAD", StringComparison.OrdinalIgnoreCase)
                ? ResolveRouteForVerb(urlRoute, "GET", requestHost)
                : null);
    }

    private IConfigurationSection? ResolveRouteForVerb(string urlRoute, string verb, string? requestHost)
    {
        if (string.IsNullOrWhiteSpace(urlRoute) || string.IsNullOrWhiteSpace(verb))
            return null;
        // Normalize inputs - remove leading/trailing slashes for consistent comparison
        // with the normalized routes in the config
        urlRoute = NormalizeRoute(urlRoute);

        // Try exact match with both route and verb, prioritized by host specificity: of the routes with
        // the most specific host, the first listed
        if (_exactRoutesByRoute.TryGetValue(urlRoute, out var sameRoute))
        {
            IConfigurationSection? exactMatch = null;
            var bestHostScore = 0;
            foreach (var rc in sameRoute)
            {
                if (!rc.Verbs.Contains(verb) || !MatchHost(rc.HostPattern, requestHost))
                    continue;
                var hostScore = GetHostSpecificityScore(rc.HostPattern, requestHost);
                if (exactMatch == null || hostScore > bestHostScore)
                {
                    exactMatch = rc.Config;
                    bestHostScore = hostScore;
                }
            }

            if (exactMatch != null)
            {
                return exactMatch;
            }
        }

        // If no exact match, try best matching route with variables
        return GetBestMatchingRouteConfig(urlRoute, verb, requestHost);
    }

    /// <summary>
    /// Resolves ALL matching route configurations for a given URL path, regardless of HTTP verb.
    /// This is primarily used for OPTIONS preflight requests where we need to know all possible
    /// verbs/methods that are allowed for a specific route.
    /// </summary>
    /// <param name="urlRoute">The URL route path to match</param>
    /// <returns>List of all matching configuration sections for the route</returns>
    public List<IConfigurationSection> ResolveRoutes(string urlRoute, string? requestHost = null)
    {
        var results = new List<IConfigurationSection>();

        if (string.IsNullOrWhiteSpace(urlRoute))
            return results;

        // Normalize inputs - remove leading/trailing slashes for consistent comparison
        urlRoute = NormalizeRoute(urlRoute);

        // Find all exact route matches (regardless of verb), filtered by host
        var exactMatches = _exactRoutes
            .Where(rc => string.Equals(rc.NormalizedRoute, urlRoute, StringComparison.OrdinalIgnoreCase)
                && MatchHost(rc.HostPattern, requestHost))
            .Select(rc => rc.Config);

        results.AddRange(exactMatches);

        // Find all matching parameterized routes
        var parameterizedMatches = GetAllMatchingRouteConfigs(urlRoute, requestHost);
        results.AddRange(parameterizedMatches);

        return results;
    }

    /// <summary>
    /// Returns ALL matching route configurations for parameterized routes based on the provided URL path.
    /// Unlike GetBestMatchingRouteConfig which returns only the best match, this returns all valid matches.
    /// </summary>
    /// <param name="normalizedUrlRoute">The normalized URL to match against config URLs</param>
    /// <returns>List of all matching route config sections</returns>
    private List<IConfigurationSection> GetAllMatchingRouteConfigs(string normalizedUrlRoute, string? requestHost = null)
    {
        var results = new List<IConfigurationSection>();

        if (string.IsNullOrWhiteSpace(normalizedUrlRoute) || _routesWithVariables == null)
            return results;

        var urlSegments = normalizedUrlRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var (NormalizedConfigRoute, Verbs, HostPattern, Config, VariablePattern) in _routesWithVariables)
        {
            if (!MatchHost(HostPattern, requestHost))
                continue;

            // Direct comparison first for performance
            if (string.Equals(normalizedUrlRoute, NormalizedConfigRoute, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Config);
                continue;
            }

            // Check if this route matches (score > 0)
            var score = CalculateRouteMatchScore(urlSegments, NormalizedConfigRoute, VariablePattern);

            if (score > 0)
            {
                results.Add(Config);
            }
        }

        return results;
    }


    public Dictionary<string, string> GetRouteParametersIfAny(
        IConfigurationSection configSection,
        string urlRoute
        )
    {
        if (configSection == null
            || !configSection.Exists()
            || string.IsNullOrWhiteSpace(urlRoute)) return [];

        var configRoute = configSection.GetValue<string>("route") ?? configSection.Key;
        if (string.IsNullOrWhiteSpace(configRoute)) return [];

        string[] urlSegments = urlRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var routeParametersRegex = RouteVariablesRegex(RouteVariablePattern(configSection));
        var (configSegments, variables) = RouteSegments(routeParametersRegex, configRoute);

        Dictionary<string, string> parameters = [];

        for (int i = 0; i < configSegments.Length && i < urlSegments.Length; i++)
        {
            if (variables[i] is string paramName)
            {
                string paramValue = urlSegments[i];

                parameters.Add(paramName, paramValue);
            }
        }

        return parameters;
    }

    /// <summary>
    /// Returns the best matching route configuration based on the provided URL path and HTTP verb.
    /// Uses a scoring algorithm to find the most specific match, prioritizing exact matches over parameter matches.
    /// </summary>
    /// <param name="normalizedUrlRoute">The normalized URL to match against config URLs</param>
    /// <param name="verb">The HTTP verb to match alongside the urlPath in config URLs</param>
    /// <returns>The best matching route config Section (only if found, otherwise return null)</returns>
    private IConfigurationSection? GetBestMatchingRouteConfig(
        string normalizedUrlRoute,
        string verb,
        string? requestHost = null
        )
    {
        if (string.IsNullOrWhiteSpace(normalizedUrlRoute)
            || string.IsNullOrWhiteSpace(verb)
            || _routesWithVariables == null)
            return null;

        IConfigurationSection? bestMatch = null;
        var bestScore = -1;
        var bestHostScore = -1;


        var urlSegments = normalizedUrlRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);


        foreach (var (NormalizedConfigRoute, Verbs, HostPattern, Config, VariablePattern) in _routesWithVariables)
        {
            // Only the routes with the verb, and the host if they name one (case insensitive)
            if (!Verbs.Contains(verb) || !MatchHost(HostPattern, requestHost))
                continue;

            // do a direct comparison first for performance
            if (string.Equals(normalizedUrlRoute, NormalizedConfigRoute, StringComparison.OrdinalIgnoreCase))
            {
                var hostScore = GetHostSpecificityScore(HostPattern, requestHost);
                if (bestMatch == null || hostScore > bestHostScore)
                {
                    bestMatch = Config;
                    bestHostScore = hostScore;
                    bestScore = int.MaxValue; // exact route match is always best
                }
                continue;
            }

            // Calculate the match score for the route config
            var score = CalculateRouteMatchScore(urlSegments, NormalizedConfigRoute, VariablePattern);

            if (score > 0)
            {
                var hostScore = GetHostSpecificityScore(HostPattern, requestHost);
                // Prefer higher route score, break ties with host specificity
                if (score > bestScore || (score == bestScore && hostScore > bestHostScore))
                {
                    bestScore = score;
                    bestHostScore = hostScore;
                    bestMatch = Config;
                }
            }
        }

        return bestScore > 0 ? bestMatch : null;
    }

    /// <summary>
    /// Calculates a match score for a given URL path against a route configuration.
    /// Higher scores indicate better matches.
    /// </summary>
    /// <param name="urlSegments">Pre-split URL segments</param>
    /// <param name="normalizedConfigRoute">Already normalized route configuration to match against</param>
    /// <param name="parameterRegex">Compiled regex for parameter detection</param>
    /// <returns>Match score (higher is better, -1 for no match)</returns>
    private static int CalculateRouteMatchScore(string[] urlSegments, string normalizedConfigRoute, Regex parameterRegex)
    {
        var (configSegments, variables) = RouteSegments(parameterRegex, normalizedConfigRoute);

        // Early exit if segment counts don't match - exact segment count required for best match
        if (urlSegments.Length != configSegments.Length)
            return -1;

        var score = 0;
        const int EXACT_MATCH_POINTS = 10;
        const int PARAMETER_MATCH_POINTS = 5;

        for (int i = 0; i < configSegments.Length; i++)
        {
            var configSegment = configSegments[i];
            var urlSegment = urlSegments[i];

            if (string.Equals(configSegment, urlSegment, StringComparison.OrdinalIgnoreCase))
            {
                // Exact match - highest score
                score += EXACT_MATCH_POINTS;
                continue;
            }

            // Check if this segment is a parameter
            if (variables[i] != null)
            {
                // Parameter segment - always matches, but lower score
                score += PARAMETER_MATCH_POINTS;
                continue;
            }

            // No match for this segment - route doesn't match
            return -1;
        }

        return score;
    }

    /// <summary>
    /// Normalizes a path by removing leading and trailing slashes and handling empty paths.
    /// </summary>
    /// <param name="route">Path to normalize</param>
    /// <returns>Normalized path</returns>
    private static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return string.Empty;

        return route.Trim('/');
    }

    /// <summary>
    /// Determines whether a request host matches a configured host pattern.
    /// Supports exact match ("www.example.com"), wildcard prefix ("*.example.com"),
    /// and null/empty pattern (matches any host).
    /// Comparison is case-insensitive. Port is expected to already be stripped by the caller.
    /// </summary>
    private static bool MatchHost(string? hostPattern, string? requestHost)
    {
        // No host constraint configured → matches every request
        if (string.IsNullOrWhiteSpace(hostPattern))
            return true;

        // Host constraint is set but the request has no host → no match
        if (string.IsNullOrWhiteSpace(requestHost))
            return false;

        // Wildcard: *.example.com → request host must end with .example.com
        if (hostPattern.StartsWith("*."))
        {
            // The suffix to match, e.g. ".example.com"
            var suffix = hostPattern.AsSpan(1); // skip the '*', keep ".example.com"
            return requestHost.EndsWith(suffix.ToString(), StringComparison.OrdinalIgnoreCase)
                && requestHost.Length > suffix.Length; // ensure there's at least one char before the suffix
        }

        // Exact host match
        return string.Equals(hostPattern, requestHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a specificity score for how well a host pattern matches the request host.
    /// Higher scores mean more specific matches, used for tie-breaking when multiple
    /// routes match the same path.
    /// 
    /// Scoring: exact host = 20, wildcard host = 10, no host constraint = 0.
    /// </summary>
    private static int GetHostSpecificityScore(string? hostPattern, string? requestHost)
    {
        if (string.IsNullOrWhiteSpace(hostPattern))
            return 0;  // No constraint — least specific

        if (hostPattern.StartsWith("*."))
            return 10; // Wildcard — medium specificity

        return 20;     // Exact — most specific
    }

}