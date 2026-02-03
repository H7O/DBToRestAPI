namespace DBToRestAPI.Services.HttpExecutor;

/// <summary>
/// Configuration options for the HTTP Request Executor service.
/// </summary>
public class HttpExecutorOptions
{
    /// <summary>
    /// Default timeout for requests when not specified in the request config.
    /// Default is 30 seconds.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Default number of retry attempts when not specified in the request config.
    /// 0 means no retries. Default is 0.
    /// </summary>
    public int DefaultRetryAttempts { get; set; } = 0;

    /// <summary>
    /// Default behavior for following redirects. Default is true.
    /// </summary>
    public bool DefaultFollowRedirects { get; set; } = true;

    /// <summary>
    /// Enable detailed request/response logging. Default is false.
    /// </summary>
    public bool EnableRequestLogging { get; set; } = false;

    /// <summary>
    /// Header name patterns to redact in logs.
    /// Default includes common sensitive headers.
    /// </summary>
    public string[] SensitiveHeaderPatterns { get; set; } = ["Authorization", "API-Key", "Token", "Secret", "Password"];

    /// <summary>
    /// Maximum response body size to buffer (in bytes).
    /// Responses larger than this will be truncated. Default is 10MB.
    /// </summary>
    public int MaxResponseBufferSizeBytes { get; set; } = 10 * 1024 * 1024;
}
