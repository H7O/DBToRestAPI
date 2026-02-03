using System.Net.Http.Headers;
using System.Text;
using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Services.HttpExecutor.Internal;

/// <summary>
/// Handles authentication for HTTP requests.
/// </summary>
internal static class AuthenticationHandler
{
    /// <summary>
    /// Applies authentication to an HttpRequestMessage based on the auth configuration.
    /// </summary>
    /// <param name="request">The HTTP request message to modify.</param>
    /// <param name="auth">The authentication configuration.</param>
    /// <param name="urlBuilder">Function to modify the URL (for API key in query string).</param>
    /// <returns>The potentially modified URL.</returns>
    public static string ApplyAuthentication(
        HttpRequestMessage request,
        HttpExecutorAuth? auth,
        string currentUrl)
    {
        if (auth == null)
            return currentUrl;

        return auth.Type.ToLowerInvariant() switch
        {
            "basic" => ApplyBasicAuth(request, auth, currentUrl),
            "bearer" => ApplyBearerAuth(request, auth, currentUrl),
            "api_key" => ApplyApiKeyAuth(request, auth, currentUrl),
            _ => currentUrl
        };
    }

    private static string ApplyBasicAuth(HttpRequestMessage request, HttpExecutorAuth auth, string url)
    {
        if (string.IsNullOrEmpty(auth.Username))
            return url;

        var credentials = $"{auth.Username}:{auth.Password ?? string.Empty}";
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);

        return url;
    }

    private static string ApplyBearerAuth(HttpRequestMessage request, HttpExecutorAuth auth, string url)
    {
        if (string.IsNullOrEmpty(auth.Token))
            return url;

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        return url;
    }

    private static string ApplyApiKeyAuth(HttpRequestMessage request, HttpExecutorAuth auth, string url)
    {
        if (string.IsNullOrEmpty(auth.Key))
            return url;

        // API key in header
        if (!string.IsNullOrEmpty(auth.KeyHeader))
        {
            request.Headers.TryAddWithoutValidation(auth.KeyHeader, auth.Key);
            return url;
        }

        // API key in query string
        if (!string.IsNullOrEmpty(auth.KeyQueryParam))
        {
            return QueryStringBuilder.AppendQueryParameter(url, auth.KeyQueryParam, auth.Key);
        }

        return url;
    }

    /// <summary>
    /// Validates that the authentication configuration has required fields.
    /// </summary>
    /// <returns>List of validation errors, empty if valid.</returns>
    public static List<string> Validate(HttpExecutorAuth? auth)
    {
        var errors = new List<string>();

        if (auth == null)
            return errors;

        switch (auth.Type.ToLowerInvariant())
        {
            case "basic":
                if (string.IsNullOrEmpty(auth.Username))
                    errors.Add("Basic auth requires 'username'");
                break;

            case "bearer":
                if (string.IsNullOrEmpty(auth.Token))
                    errors.Add("Bearer auth requires 'token'");
                break;

            case "api_key":
                if (string.IsNullOrEmpty(auth.Key))
                    errors.Add("API key auth requires 'key'");
                if (string.IsNullOrEmpty(auth.KeyHeader) && string.IsNullOrEmpty(auth.KeyQueryParam))
                    errors.Add("API key auth requires either 'key_header' or 'key_query_param'");
                break;

            default:
                errors.Add($"Unknown auth type: '{auth.Type}'. Valid types are: basic, bearer, api_key");
                break;
        }

        return errors;
    }
}
