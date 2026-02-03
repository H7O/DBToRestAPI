namespace DBToRestAPI.Services.HttpExecutor.Models;

/// <summary>
/// Retry policy configuration for HTTP requests.
/// </summary>
public class HttpExecutorRetry
{
    /// <summary>
    /// Maximum number of retry attempts. Default is 3.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Base delay between retries in milliseconds. Default is 1000ms.
    /// </summary>
    public int DelayMs { get; init; } = 1000;

    /// <summary>
    /// Whether to use exponential backoff (double delay after each retry). Default is true.
    /// </summary>
    public bool ExponentialBackoff { get; init; } = true;

    /// <summary>
    /// HTTP status codes that should trigger a retry. Default is [500, 502, 503, 504].
    /// </summary>
    public int[]? RetryStatusCodes { get; init; }

    /// <summary>
    /// Gets the default retry status codes.
    /// </summary>
    public static int[] DefaultRetryStatusCodes => [500, 502, 503, 504];
}
