using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Services.HttpExecutor.Internal;

/// <summary>
/// Handles retry logic with exponential backoff.
/// </summary>
internal class RetryHandler
{
    private readonly HttpExecutorRetry _retryConfig;
    private readonly HashSet<int> _retryStatusCodes;

    public RetryHandler(HttpExecutorRetry? retryConfig, int defaultMaxAttempts)
    {
        // Always need at least 1 attempt for the initial request
        var maxAttempts = retryConfig?.MaxAttempts ?? (defaultMaxAttempts > 0 ? defaultMaxAttempts : 1);
        _retryConfig = retryConfig ?? new HttpExecutorRetry { MaxAttempts = Math.Max(1, maxAttempts) };

        // Ensure at least 1 attempt
        if (_retryConfig.MaxAttempts < 1)
        {
            _retryConfig = new HttpExecutorRetry
            {
                MaxAttempts = 1,
                DelayMs = _retryConfig.DelayMs,
                ExponentialBackoff = _retryConfig.ExponentialBackoff,
                RetryStatusCodes = _retryConfig.RetryStatusCodes
            };
        }

        _retryStatusCodes = _retryConfig.RetryStatusCodes?.ToHashSet()
            ?? HttpExecutorRetry.DefaultRetryStatusCodes.ToHashSet();
    }

    /// <summary>
    /// Gets the maximum number of retry attempts.
    /// </summary>
    public int MaxAttempts => _retryConfig.MaxAttempts;

    /// <summary>
    /// Determines if a retry should be attempted based on the status code.
    /// </summary>
    /// <param name="statusCode">The HTTP status code from the response.</param>
    /// <param name="currentAttempt">The current attempt number (1-based).</param>
    /// <returns>True if a retry should be attempted.</returns>
    public bool ShouldRetry(int statusCode, int currentAttempt)
    {
        if (currentAttempt >= _retryConfig.MaxAttempts)
            return false;

        return _retryStatusCodes.Contains(statusCode);
    }

    /// <summary>
    /// Determines if a retry should be attempted after an exception.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="currentAttempt">The current attempt number (1-based).</param>
    /// <returns>True if a retry should be attempted.</returns>
    public bool ShouldRetryOnException(Exception exception, int currentAttempt)
    {
        if (currentAttempt >= _retryConfig.MaxAttempts)
            return false;

        // Retry on transient network errors
        return exception is HttpRequestException
            || exception is TaskCanceledException { InnerException: TimeoutException };
    }

    /// <summary>
    /// Gets the delay before the next retry attempt.
    /// </summary>
    /// <param name="attemptNumber">The attempt number (1-based) that just failed.</param>
    /// <returns>The delay before the next attempt.</returns>
    public TimeSpan GetDelay(int attemptNumber)
    {
        var delayMs = _retryConfig.DelayMs;

        if (_retryConfig.ExponentialBackoff && attemptNumber > 1)
        {
            // Exponential backoff: delay * 2^(attempt-1)
            delayMs = (int)(delayMs * Math.Pow(2, attemptNumber - 1));
        }

        // Cap at 60 seconds
        delayMs = Math.Min(delayMs, 60000);

        return TimeSpan.FromMilliseconds(delayMs);
    }

    /// <summary>
    /// Waits for the retry delay.
    /// </summary>
    public async Task WaitAsync(int attemptNumber, CancellationToken cancellationToken)
    {
        var delay = GetDelay(attemptNumber);
        await Task.Delay(delay, cancellationToken);
    }
}
