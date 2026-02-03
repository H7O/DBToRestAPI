using System.Text.Json;
using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Services.HttpExecutor.Internal;

/// <summary>
/// Parses JSON request configurations into HttpExecutorRequest objects.
/// </summary>
internal static class JsonRequestParser
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Parses a JSON string into an HttpExecutorRequest.
    /// </summary>
    public static HttpExecutorRequest Parse(string json)
    {
        using var document = JsonDocument.Parse(json, _documentOptions);
        return Parse(document.RootElement);
    }

    /// <summary>
    /// Parses a JsonElement into an HttpExecutorRequest.
    /// </summary>
    public static HttpExecutorRequest Parse(JsonElement element)
    {
        var url = element.GetPropertyOrDefault("url")?.GetString()
            ?? throw new ArgumentException("Missing required property: 'url'");


        var method = element.GetPropertyOrDefault("method")?.GetString() ?? "GET";
        var contentType = element.GetPropertyOrDefault("content_type")?.GetString() ?? "application/json";
        var timeoutSeconds = element.GetPropertyOrDefault("timeout_seconds")?.GetInt32() ?? 30;
        var ignoreCertErrors = element.GetPropertyOrDefault("ignore_certificate_errors")?.GetBoolean() ?? false;
        var followRedirects = element.GetPropertyOrDefault("follow_redirects")?.GetBoolean() ?? true;
        var bodyRaw = element.GetPropertyOrDefault("body_raw")?.GetString();

        // Parse headers
        Dictionary<string, string>? headers = null;
        if (element.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
        {
            headers = [];
            foreach (var prop in headersElement.EnumerateObject())
            {
                headers[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        // Parse query parameters
        Dictionary<string, string>? query = null;
        if (element.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.Object)
        {
            query = [];
            foreach (var prop in queryElement.EnumerateObject())
            {
                query[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        // Parse body (keep as JsonElement for later serialization)
        object? body = null;
        if (element.TryGetProperty("body", out var bodyElement) &&
            bodyElement.ValueKind != JsonValueKind.Null)
        {
            body = bodyElement.Clone();
        }

        // Parse auth
        HttpExecutorAuth? auth = null;
        if (element.TryGetProperty("auth", out var authElement) && authElement.ValueKind == JsonValueKind.Object)
        {
            var authType = authElement.GetPropertyOrDefault("type")?.GetString();
            if (!string.IsNullOrEmpty(authType))
            {
                auth = new HttpExecutorAuth
                {
                    Type = authType,
                    Username = authElement.GetPropertyOrDefault("username")?.GetString(),
                    Password = authElement.GetPropertyOrDefault("password")?.GetString(),
                    Token = authElement.GetPropertyOrDefault("token")?.GetString(),
                    Key = authElement.GetPropertyOrDefault("key")?.GetString(),
                    KeyHeader = authElement.GetPropertyOrDefault("key_header")?.GetString(),
                    KeyQueryParam = authElement.GetPropertyOrDefault("key_query_param")?.GetString()
                };
            }
        }

        // Parse retry
        HttpExecutorRetry? retry = null;
        if (element.TryGetProperty("retry", out var retryElement) && retryElement.ValueKind == JsonValueKind.Object)
        {
            int[]? retryStatusCodes = null;
            if (retryElement.TryGetProperty("retry_status_codes", out var statusCodesElement) &&
                statusCodesElement.ValueKind == JsonValueKind.Array)
            {
                retryStatusCodes = statusCodesElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => e.GetInt32())
                    .ToArray();
            }

            retry = new HttpExecutorRetry
            {
                MaxAttempts = retryElement.GetPropertyOrDefault("max_attempts")?.GetInt32() ?? 3,
                DelayMs = retryElement.GetPropertyOrDefault("delay_ms")?.GetInt32() ?? 1000,
                ExponentialBackoff = retryElement.GetPropertyOrDefault("exponential_backoff")?.GetBoolean() ?? true,
                RetryStatusCodes = retryStatusCodes
            };
        }

        return new HttpExecutorRequest
        {
            Url = url,
            Method = method.ToUpperInvariant(),
            Headers = headers,
            Query = query,
            Body = body,
            BodyRaw = bodyRaw,
            ContentType = contentType,
            TimeoutSeconds = timeoutSeconds,
            IgnoreCertificateErrors = ignoreCertErrors,
            FollowRedirects = followRedirects,
            Auth = auth,
            Retry = retry
        };
    }

    /// <summary>
    /// Gets a property value or returns null if not found.
    /// </summary>
    private static JsonElement? GetPropertyOrDefault(this JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value))
            return value;
        return null;
    }
}
