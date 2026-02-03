using System.Diagnostics;
using System.Text.Json;
using DBToRestAPI.Services.HttpExecutor.Internal;
using DBToRestAPI.Services.HttpExecutor.Models;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Services.HttpExecutor;

/// <summary>
/// Implementation of IHttpRequestExecutor that provides curl-like HTTP request execution.
/// </summary>
public class HttpRequestExecutor : IHttpRequestExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpExecutorOptions _options;
    private readonly ILogger<HttpRequestExecutor> _logger;

    public HttpRequestExecutor(
        IHttpClientFactory httpClientFactory,
        HttpExecutorOptions options,
        ILogger<HttpRequestExecutor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HttpExecutorResponse> ExecuteAsync(
        string requestJson,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var request = JsonRequestParser.Parse(requestJson);
            return await ExecuteInternalAsync(request, stopwatch, cancellationToken);
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to parse request JSON");
            return HttpExecutorResponse.FromError(
                $"Invalid JSON: {ex.Message}",
                ex,
                stopwatch.Elapsed,
                0);
        }
        catch (ArgumentException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Invalid request configuration");
            return HttpExecutorResponse.FromError(
                ex.Message,
                ex,
                stopwatch.Elapsed,
                0);
        }
    }

    /// <inheritdoc />
    public async Task<HttpExecutorResponse> ExecuteAsync(
        JsonElement requestConfig,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var request = JsonRequestParser.Parse(requestConfig);
            return await ExecuteInternalAsync(request, stopwatch, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Invalid request configuration");
            return HttpExecutorResponse.FromError(
                ex.Message,
                ex,
                stopwatch.Elapsed,
                0);
        }
    }

    /// <inheritdoc />
    public Task<HttpExecutorResponse> ExecuteAsync(
        HttpExecutorRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        return ExecuteInternalAsync(request, stopwatch, cancellationToken);
    }

    /// <inheritdoc />
    public HttpExecutorValidationResult Validate(string requestJson)
    {
        return RequestValidator.Validate(requestJson);
    }

    private async Task<HttpExecutorResponse> ExecuteInternalAsync(
        HttpExecutorRequest request,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var retryHandler = new RetryHandler(request.Retry, _options.DefaultRetryAttempts);
        var currentAttempt = 0;
        HttpResponseMessage? lastResponse = null;
        Exception? lastException = null;

        while (currentAttempt < retryHandler.MaxAttempts)
        {
            currentAttempt++;

            try
            {
                using var httpRequest = RequestMessageBuilder.Build(request);

                LogRequest(request, currentAttempt);

                var client = GetClient(request);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

                lastResponse = await client.SendAsync(httpRequest, cts.Token);

                LogResponse(request, lastResponse, stopwatch.Elapsed);

                // Check if we should retry
                if (retryHandler.ShouldRetry((int)lastResponse.StatusCode, currentAttempt))
                {
                    _logger.LogWarning(
                        "HttpExecutor: {Method} {Url} -> {StatusCode}, retrying ({Attempt}/{MaxAttempts})...",
                        request.Method,
                        request.Url,
                        (int)lastResponse.StatusCode,
                        currentAttempt,
                        retryHandler.MaxAttempts);

                    await retryHandler.WaitAsync(currentAttempt, cancellationToken);
                    lastResponse.Dispose();
                    continue;
                }

                // Success or non-retryable status
                stopwatch.Stop();
                return await HttpExecutorResponse.FromHttpResponseAsync(
                    lastResponse,
                    stopwatch.Elapsed,
                    currentAttempt - 1,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _logger.LogWarning("HttpExecutor: Request cancelled for {Url}", request.Url);
                return HttpExecutorResponse.FromError(
                    "Request was cancelled",
                    null,
                    stopwatch.Elapsed,
                    currentAttempt - 1);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                _logger.LogWarning(
                    "HttpExecutor: Request timeout for {Url} after {Timeout}s",
                    request.Url,
                    request.TimeoutSeconds);

                if (retryHandler.ShouldRetryOnException(ex, currentAttempt))
                {
                    await retryHandler.WaitAsync(currentAttempt, cancellationToken);
                    continue;
                }

                stopwatch.Stop();
                return HttpExecutorResponse.FromError(
                    $"Request timed out after {request.TimeoutSeconds} seconds",
                    ex,
                    stopwatch.Elapsed,
                    currentAttempt - 1);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger.LogError(ex, "HttpExecutor: Request failed for {Url}", request.Url);

                if (retryHandler.ShouldRetryOnException(ex, currentAttempt))
                {
                    _logger.LogWarning(
                        "HttpExecutor: Connection error, retrying ({Attempt}/{MaxAttempts})...",
                        currentAttempt,
                        retryHandler.MaxAttempts);

                    await retryHandler.WaitAsync(currentAttempt, cancellationToken);
                    continue;
                }

                stopwatch.Stop();
                return HttpExecutorResponse.FromError(
                    GetFriendlyErrorMessage(ex),
                    ex,
                    stopwatch.Elapsed,
                    currentAttempt - 1);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "HttpExecutor: Unexpected error for {Url}", request.Url);
                return HttpExecutorResponse.FromError(
                    $"Unexpected error: {ex.Message}",
                    ex,
                    stopwatch.Elapsed,
                    currentAttempt - 1);
            }
        }

        // All retries exhausted
        stopwatch.Stop();

        if (lastResponse != null)
        {
            return await HttpExecutorResponse.FromHttpResponseAsync(
                lastResponse,
                stopwatch.Elapsed,
                currentAttempt - 1,
                cancellationToken);
        }

        return HttpExecutorResponse.FromError(
            lastException?.Message ?? "Request failed after all retry attempts",
            lastException,
            stopwatch.Elapsed,
            currentAttempt - 1);
    }

    private HttpClient GetClient(HttpExecutorRequest request)
    {
        var clientName = (request.IgnoreCertificateErrors, request.FollowRedirects) switch
        {
            (true, false) => "HttpExecutor.IgnoreCerts.NoRedirect",
            (true, true) => "HttpExecutor.IgnoreCerts",
            (false, false) => "HttpExecutor.NoRedirect",
            (false, true) => "HttpExecutor"
        };

        return _httpClientFactory.CreateClient(clientName);
    }

    private void LogRequest(HttpExecutorRequest request, int attempt)
    {
        if (!_options.EnableRequestLogging)
        {
            _logger.LogInformation(
                "HttpExecutor: {Method} {Url}{Retry}",
                request.Method,
                request.Url,
                attempt > 1 ? $" (attempt {attempt})" : "");
            return;
        }

        var headers = request.Headers != null
            ? RedactSensitiveHeaders(request.Headers)
            : "none";

        _logger.LogDebug(
            "HttpExecutor: {Method} {Url} - Headers: {Headers}{Retry}",
            request.Method,
            request.Url,
            headers,
            attempt > 1 ? $" (attempt {attempt})" : "");
    }

    private void LogResponse(HttpExecutorRequest request, HttpResponseMessage response, TimeSpan elapsed)
    {
        var logLevel = response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Warning;

        _logger.Log(
            logLevel,
            "HttpExecutor: {Method} {Url} -> {StatusCode} {ReasonPhrase} ({ElapsedMs}ms)",
            request.Method,
            request.Url,
            (int)response.StatusCode,
            response.ReasonPhrase,
            elapsed.TotalMilliseconds.ToString("F0"));
    }

    private string RedactSensitiveHeaders(Dictionary<string, string> headers)
    {
        var redacted = headers.ToDictionary(
            h => h.Key,
            h => IsSensitiveHeader(h.Key) ? "[REDACTED]" : h.Value);

        return System.Text.Json.JsonSerializer.Serialize(redacted);
    }

    private bool IsSensitiveHeader(string headerName)
    {
        return _options.SensitiveHeaderPatterns.Any(pattern =>
            headerName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetFriendlyErrorMessage(HttpRequestException ex)
    {
        var message = ex.Message;

        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
        {
            return $"Host not found: {ex.Message}";
        }

        if (message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection refused - the server may be down or not accepting connections";
        }

        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return $"SSL/Certificate error: {ex.Message}. Consider setting 'ignore_certificate_errors' to true for development.";
        }

        return ex.Message;
    }
}
