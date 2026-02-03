namespace DBToRestAPI.Services.HttpExecutor.Models;

/// <summary>
/// Strongly-typed HTTP request configuration.
/// </summary>
public class HttpExecutorRequest
{
    /// <summary>
    /// Target URL for the HTTP request. Required.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// HTTP method (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS). Default is "GET".
    /// </summary>
    public string Method { get; init; } = "GET";

    /// <summary>
    /// Custom headers to include in the request.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Query parameters to append to the URL.
    /// </summary>
    public Dictionary<string, string>? Query { get; init; }

    /// <summary>
    /// JSON body object (will be serialized automatically).
    /// </summary>
    public object? Body { get; init; }

    /// <summary>
    /// Raw string body (used if Body is null).
    /// </summary>
    public string? BodyRaw { get; init; }

    /// <summary>
    /// Content-Type header value. Default is "application/json".
    /// </summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>
    /// Request timeout in seconds. Default is 30.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether to skip SSL certificate validation. Default is false.
    /// </summary>
    public bool IgnoreCertificateErrors { get; init; } = false;

    /// <summary>
    /// Whether to automatically follow HTTP redirects. Default is true.
    /// </summary>
    public bool FollowRedirects { get; init; } = true;

    /// <summary>
    /// Authentication configuration.
    /// </summary>
    public HttpExecutorAuth? Auth { get; init; }

    /// <summary>
    /// Retry policy configuration.
    /// </summary>
    public HttpExecutorRetry? Retry { get; init; }
}
