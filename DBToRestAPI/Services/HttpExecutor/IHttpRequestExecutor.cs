using System.Text.Json;
using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Services.HttpExecutor;

/// <summary>
/// Service for executing HTTP requests defined in JSON format.
/// Provides a curl-like interface for making HTTP calls programmatically.
/// </summary>
public interface IHttpRequestExecutor
{
    /// <summary>
    /// Executes an HTTP request defined by a JSON string.
    /// </summary>
    /// <param name="requestJson">JSON string containing the request configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response containing status, headers, and content.</returns>
    /// <remarks>
    /// Errors the call meets, timeouts included, are captured in the response. It throws an
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled,
    /// and may throw for a request description it can't read.
    /// </remarks>
    Task<HttpExecutorResponse> ExecuteAsync(
        string requestJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an HTTP request defined by a JsonElement.
    /// </summary>
    /// <param name="requestConfig">JsonElement containing the request configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response containing status, headers, and content.</returns>
    /// <remarks>
    /// Errors the call meets, timeouts included, are captured in the response. It throws an
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled,
    /// and may throw for a request description it can't read.
    /// </remarks>
    Task<HttpExecutorResponse> ExecuteAsync(
        JsonElement requestConfig,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an HTTP request defined by a strongly-typed object.
    /// </summary>
    /// <param name="request">The request configuration object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response containing status, headers, and content.</returns>
    /// <remarks>
    /// Errors the call meets, timeouts included, are captured in the response. It throws an
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled,
    /// and may throw for a request description it can't read.
    /// </remarks>
    Task<HttpExecutorResponse> ExecuteAsync(
        HttpExecutorRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a JSON request configuration without executing it.
    /// </summary>
    /// <param name="requestJson">JSON string containing the request configuration.</param>
    /// <returns>Validation result with any errors or warnings.</returns>
    HttpExecutorValidationResult Validate(string requestJson);
}
