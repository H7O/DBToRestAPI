using System.Web;

namespace DBToRestAPI.Services.HttpExecutor.Internal;

/// <summary>
/// Builds and merges query strings for URLs.
/// </summary>
internal static class QueryStringBuilder
{
    /// <summary>
    /// Appends query parameters to a URL.
    /// </summary>
    /// <param name="url">The base URL (may already contain query parameters).</param>
    /// <param name="queryParams">Additional query parameters to append.</param>
    /// <returns>The URL with query parameters appended.</returns>
    public static string AppendQueryParameters(string url, Dictionary<string, string>? queryParams)
    {
        if (queryParams == null || queryParams.Count == 0)
            return url;

        var separator = url.Contains('?') ? "&" : "?";
        var queryString = string.Join("&", queryParams.Select(kvp =>
            $"{HttpUtility.UrlEncode(kvp.Key)}={HttpUtility.UrlEncode(kvp.Value)}"));

        return $"{url}{separator}{queryString}";
    }

    /// <summary>
    /// Appends a single query parameter to a URL.
    /// </summary>
    /// <param name="url">The base URL (may already contain query parameters).</param>
    /// <param name="key">The parameter key.</param>
    /// <param name="value">The parameter value.</param>
    /// <returns>The URL with the query parameter appended.</returns>
    public static string AppendQueryParameter(string url, string key, string value)
    {
        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}{HttpUtility.UrlEncode(key)}={HttpUtility.UrlEncode(value)}";
    }
}
