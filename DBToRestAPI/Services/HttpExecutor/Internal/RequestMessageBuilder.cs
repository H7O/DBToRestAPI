using System.Text;
using System.Text.Json;
using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Services.HttpExecutor.Internal;

/// <summary>
/// Builds HttpRequestMessage from HttpExecutorRequest configuration.
/// </summary>
internal static class RequestMessageBuilder
{
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "Content-Length",
        "Content-Encoding",
        "Content-Language",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Content-Disposition"
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Builds an HttpRequestMessage from the request configuration.
    /// </summary>
    public static HttpRequestMessage Build(HttpExecutorRequest request)
    {
        // Build URL with query parameters
        var url = QueryStringBuilder.AppendQueryParameters(request.Url, request.Query);

        // Create the request message
        var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method), url);

        // Apply authentication (may modify URL for API key in query string)
        url = AuthenticationHandler.ApplyAuthentication(httpRequest, request.Auth, url);
        if (url != request.Url)
        {
            httpRequest.RequestUri = new Uri(url);
        }

        // Set content
        SetContent(httpRequest, request);

        // Set headers
        SetHeaders(httpRequest, request);

        return httpRequest;
    }

    private static void SetContent(HttpRequestMessage httpRequest, HttpExecutorRequest request)
    {
        // Check if there's a body to send
        if (request.Body == null && string.IsNullOrEmpty(request.BodyRaw))
            return;

        string contentString;

        if (request.Body != null)
        {
            // Serialize the body to JSON
            if (request.Body is JsonElement jsonElement)
            {
                contentString = jsonElement.GetRawText();
            }
            else
            {
                contentString = JsonSerializer.Serialize(request.Body, _jsonOptions);
            }
        }
        else
        {
            contentString = request.BodyRaw!;
        }

        httpRequest.Content = new StringContent(contentString, Encoding.UTF8, request.ContentType);
    }

    private static void SetHeaders(HttpRequestMessage httpRequest, HttpExecutorRequest request)
    {
        if (request.Headers == null)
            return;

        foreach (var header in request.Headers)
        {
            if (ContentHeaderNames.Contains(header.Key))
            {
                // Content headers must be set on the content
                httpRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            else
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }
}
