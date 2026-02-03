namespace DBToRestAPI.Services.HttpExecutor.Models;

/// <summary>
/// Authentication configuration for HTTP requests.
/// </summary>
public class HttpExecutorAuth
{
    /// <summary>
    /// Authentication type: "basic", "bearer", or "api_key".
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Username for Basic authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Password for Basic authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Token for Bearer authentication.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// API key value.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// Header name to place the API key (e.g., "X-API-Key").
    /// </summary>
    public string? KeyHeader { get; init; }

    /// <summary>
    /// Query parameter name to place the API key (e.g., "api_key").
    /// </summary>
    public string? KeyQueryParam { get; init; }
}
