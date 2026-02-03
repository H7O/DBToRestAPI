using System.Text;
using System.Text.Json;

namespace DBToRestAPI.Services.HttpExecutor.Models;

/// <summary>
/// Response container for HTTP request execution results.
/// </summary>
public class HttpExecutorResponse
{
    /// <summary>
    /// Indicates whether the HTTP request completed with a success status code (2xx).
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// The HTTP status code returned by the server. 0 if request failed before receiving response.
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// The HTTP reason phrase (e.g., "OK", "Not Found").
    /// </summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>
    /// Response headers (excluding content headers).
    /// </summary>
    public Dictionary<string, string[]> Headers { get; init; } = [];

    /// <summary>
    /// Content-specific headers (Content-Type, Content-Length, etc.).
    /// </summary>
    public Dictionary<string, string[]> ContentHeaders { get; init; } = [];

    /// <summary>
    /// The raw response body as bytes.
    /// </summary>
    public byte[] Content { get; init; } = [];

    /// <summary>
    /// The response body decoded as UTF-8 string.
    /// </summary>
    public string ContentAsString => Encoding.UTF8.GetString(Content);

    /// <summary>
    /// Total time elapsed for the request (including retries).
    /// </summary>
    public TimeSpan ElapsedTime { get; init; }

    /// <summary>
    /// Number of retry attempts made (0 if succeeded on first try).
    /// </summary>
    public int RetryAttempts { get; init; }

    /// <summary>
    /// Error message if the request failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The exception that caused the failure, if any.
    /// </summary>
    public Exception? Exception { get; init; }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Deserializes the response body to the specified type.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <returns>The deserialized object, or null if deserialization fails.</returns>
    public T? ContentAs<T>() where T : class
    {
        if (Content.Length == 0)
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(Content, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the response body as JSON.
    /// </summary>
    /// <returns>The parsed JsonElement, or null if parsing fails.</returns>
    public JsonElement? ContentAsJson()
    {
        if (Content.Length == 0)
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(Content);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a success response from an HttpResponseMessage.
    /// </summary>
    internal static async Task<HttpExecutorResponse> FromHttpResponseAsync(
        HttpResponseMessage response,
        TimeSpan elapsed,
        int retryAttempts,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var headers = response.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToArray());

        var contentHeaders = response.Content.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToArray());

        return new HttpExecutorResponse
        {
            IsSuccess = response.IsSuccessStatusCode,
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Headers = headers,
            ContentHeaders = contentHeaders,
            Content = content,
            ElapsedTime = elapsed,
            RetryAttempts = retryAttempts
        };
    }

    /// <summary>
    /// Creates an error response.
    /// </summary>
    internal static HttpExecutorResponse FromError(
        string errorMessage,
        Exception? exception,
        TimeSpan elapsed,
        int retryAttempts,
        int statusCode = 0)
    {
        return new HttpExecutorResponse
        {
            IsSuccess = false,
            StatusCode = statusCode,
            ErrorMessage = errorMessage,
            Exception = exception,
            ElapsedTime = elapsed,
            RetryAttempts = retryAttempts
        };
    }
}
