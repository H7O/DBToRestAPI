namespace DBToRestAPI.Services.HttpExecutor.Models;

/// <summary>
/// Result of validating an HTTP request configuration.
/// </summary>
public class HttpExecutorValidationResult
{
    /// <summary>
    /// Whether the configuration is valid.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// List of validation errors.
    /// </summary>
    public List<string> Errors { get; init; } = [];

    /// <summary>
    /// List of validation warnings (non-blocking issues).
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static HttpExecutorValidationResult Success() => new() { IsValid = true };

    /// <summary>
    /// Creates a failed validation result with errors.
    /// </summary>
    public static HttpExecutorValidationResult Failure(params string[] errors) => new()
    {
        IsValid = false,
        Errors = [.. errors]
    };

    /// <summary>
    /// Creates a failed validation result with errors and warnings.
    /// </summary>
    public static HttpExecutorValidationResult Failure(IEnumerable<string> errors, IEnumerable<string>? warnings = null) => new()
    {
        IsValid = false,
        Errors = errors.ToList(),
        Warnings = warnings?.ToList() ?? []
    };
}
