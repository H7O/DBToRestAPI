using System.Text.Json;
using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Services.HttpExecutor.Internal;

/// <summary>
/// Validates HTTP request configurations.
/// </summary>
internal static class RequestValidator
{
    private static readonly HashSet<string> ValidMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"
    };

    /// <summary>
    /// Validates a JSON request configuration.
    /// </summary>
    public static HttpExecutorValidationResult Validate(string json)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Try to parse JSON
        JsonElement element;
        try
        {
            using var document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return HttpExecutorValidationResult.Failure($"Invalid JSON: {ex.Message}");
        }

        // Validate root is object
        if (element.ValueKind != JsonValueKind.Object)
        {
            return HttpExecutorValidationResult.Failure("Request configuration must be a JSON object");
        }

        // Validate URL (required)
        if (!element.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(urlElement.GetString()))
        {
            errors.Add("Missing or empty required property: 'url'");
        }
        else
        {
            var url = urlElement.GetString()!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                errors.Add($"Invalid URL format: '{url}'");
            }
            else if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                errors.Add($"URL must use http or https scheme, got: '{uri.Scheme}'");
            }
        }

        // Validate method (optional, default GET)
        if (element.TryGetProperty("method", out var methodElement))
        {
            if (methodElement.ValueKind != JsonValueKind.String)
            {
                errors.Add("Property 'method' must be a string");
            }
            else
            {
                var method = methodElement.GetString();
                if (!ValidMethods.Contains(method ?? string.Empty))
                {
                    errors.Add($"Invalid HTTP method: '{method}'. Valid methods: {string.Join(", ", ValidMethods)}");
                }
            }
        }

        // Validate timeout_seconds (optional)
        if (element.TryGetProperty("timeout_seconds", out var timeoutElement))
        {
            if (timeoutElement.ValueKind != JsonValueKind.Number)
            {
                errors.Add("Property 'timeout_seconds' must be a number");
            }
            else
            {
                var timeout = timeoutElement.GetInt32();
                if (timeout < 1 || timeout > 300)
                {
                    errors.Add("Property 'timeout_seconds' must be between 1 and 300");
                }
            }
        }

        // Validate headers (optional)
        if (element.TryGetProperty("headers", out var headersElement))
        {
            if (headersElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Property 'headers' must be an object with string values");
            }
        }

        // Validate query (optional)
        if (element.TryGetProperty("query", out var queryElement))
        {
            if (queryElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Property 'query' must be an object with string values");
            }
        }

        // Validate auth (optional)
        if (element.TryGetProperty("auth", out var authElement) && authElement.ValueKind != JsonValueKind.Null)
        {
            if (authElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Property 'auth' must be an object");
            }
            else
            {
                ValidateAuth(authElement, errors);
            }
        }

        // Validate retry (optional)
        if (element.TryGetProperty("retry", out var retryElement) && retryElement.ValueKind != JsonValueKind.Null)
        {
            if (retryElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Property 'retry' must be an object");
            }
            else
            {
                ValidateRetry(retryElement, errors, warnings);
            }
        }

        // Check for body and body_raw conflict
        var hasBody = element.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind != JsonValueKind.Null;
        var hasBodyRaw = element.TryGetProperty("body_raw", out var bodyRawEl) && bodyRawEl.ValueKind != JsonValueKind.Null;
        if (hasBody && hasBodyRaw)
        {
            warnings.Add("Both 'body' and 'body_raw' are specified. 'body' will take precedence.");
        }

        if (errors.Count > 0)
            return HttpExecutorValidationResult.Failure(errors, warnings);

        return new HttpExecutorValidationResult
        {
            IsValid = true,
            Warnings = warnings
        };
    }

    private static void ValidateAuth(JsonElement authElement, List<string> errors)
    {
        if (!authElement.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(typeElement.GetString()))
        {
            errors.Add("Auth object requires 'type' property");
            return;
        }

        var authType = typeElement.GetString()!.ToLowerInvariant();

        switch (authType)
        {
            case "basic":
                if (!authElement.TryGetProperty("username", out _))
                    errors.Add("Basic auth requires 'username' property");
                break;

            case "bearer":
                if (!authElement.TryGetProperty("token", out _))
                    errors.Add("Bearer auth requires 'token' property");
                break;

            case "api_key":
                if (!authElement.TryGetProperty("key", out _))
                    errors.Add("API key auth requires 'key' property");

                var hasKeyHeader = authElement.TryGetProperty("key_header", out var kh) &&
                    kh.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(kh.GetString());
                var hasKeyQueryParam = authElement.TryGetProperty("key_query_param", out var kq) &&
                    kq.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(kq.GetString());

                if (!hasKeyHeader && !hasKeyQueryParam)
                    errors.Add("API key auth requires either 'key_header' or 'key_query_param'");
                break;

            default:
                errors.Add($"Unknown auth type: '{authType}'. Valid types: basic, bearer, api_key");
                break;
        }
    }

    private static void ValidateRetry(JsonElement retryElement, List<string> errors, List<string> warnings)
    {
        if (retryElement.TryGetProperty("max_attempts", out var maxAttemptsEl))
        {
            if (maxAttemptsEl.ValueKind != JsonValueKind.Number)
            {
                errors.Add("Retry 'max_attempts' must be a number");
            }
            else
            {
                var maxAttempts = maxAttemptsEl.GetInt32();
                if (maxAttempts < 1 || maxAttempts > 10)
                {
                    errors.Add("Retry 'max_attempts' must be between 1 and 10");
                }
            }
        }

        if (retryElement.TryGetProperty("delay_ms", out var delayEl))
        {
            if (delayEl.ValueKind != JsonValueKind.Number)
            {
                errors.Add("Retry 'delay_ms' must be a number");
            }
            else
            {
                var delayMs = delayEl.GetInt32();
                if (delayMs < 100 || delayMs > 60000)
                {
                    errors.Add("Retry 'delay_ms' must be between 100 and 60000");
                }
            }
        }

        if (retryElement.TryGetProperty("retry_status_codes", out var statusCodesEl))
        {
            if (statusCodesEl.ValueKind != JsonValueKind.Array)
            {
                errors.Add("Retry 'retry_status_codes' must be an array of integers");
            }
            else if (statusCodesEl.GetArrayLength() == 0)
            {
                warnings.Add("Retry 'retry_status_codes' is empty. Default status codes will be used.");
            }
        }
    }
}
