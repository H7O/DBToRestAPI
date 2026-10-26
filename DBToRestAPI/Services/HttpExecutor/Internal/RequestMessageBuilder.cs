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
        // A form's fields, encoded as a query's are. The parser lets no body or body_raw beside it.
        if (request.Form != null)
        {
            httpRequest.Content = new StringContent(QueryStringBuilder.Encode(request.Form), Encoding.UTF8, request.ContentType);
            return;
        }

        // Check if there's a body to send
        if (request.Body == null && string.IsNullOrEmpty(request.BodyRaw))
            return;

        string contentString;

        if (request.Body != null)
        {
            // Serialize the body to JSON. A string body sent as another type (a form, say) is
            // that text, so it is sent as written, like body_raw, not as a JSON string in quotes.
            if (request.Body is JsonElement { ValueKind: JsonValueKind.String } textElement
                && !IsJson(EffectiveContentType(request)))
            {
                contentString = textElement.GetString()!;
            }
            else if (request.Body is JsonElement jsonElement)
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

    /// <summary>The body's type: a Content-Type in headers, which replaces content_type, else content_type.</summary>
    private static string EffectiveContentType(HttpExecutorRequest request)
        => request.Headers?.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value
           ?? request.ContentType;

    private static bool IsJson(string? contentType)
    {
        var mediaType = contentType?.Split(';')[0].Trim();
        return string.IsNullOrEmpty(mediaType)
            || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetHeaders(HttpRequestMessage httpRequest, HttpExecutorRequest request)
    {
        if (request.Headers == null)
            return;

        foreach (var header in request.Headers)
        {
            // A carriage return or line feed in a header would let a substituted value start
            // a header of its own, or a request body. TryAddWithoutValidation is used below
            // precisely because it skips the framework's own checks, so nothing else refuses
            // this - the header is dropped rather than sent.
            if (EmbeddedHttpTemplate.ContainsHeaderBreak(header.Key)
                || EmbeddedHttpTemplate.ContainsHeaderBreak(header.Value))
            {
                continue;
            }

            if (ContentHeaderNames.Contains(header.Key))
            {
                // Content headers must be set on the content. The header replaces the content's
                // own value: added beside it, a Content-Type would be sent as two types.
                httpRequest.Content?.Headers.Remove(header.Key);
                httpRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            else
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }
}
