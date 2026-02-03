using DBToRestAPI.Services.HttpExecutor.Internal;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Unit tests for request validation functionality.
/// </summary>
public class RequestValidatorTests
{
    [Fact]
    public void Validate_ValidMinimalRequest_ReturnsSuccess()
    {
        // Arrange
        var json = """{"url": "https://example.com/api"}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_ValidFullRequest_ReturnsSuccess()
    {
        // Arrange
        var json = """
        {
            "url": "https://api.example.com/users",
            "method": "POST",
            "headers": {"X-Custom": "value"},
            "query": {"page": "1"},
            "body": {"name": "John"},
            "timeout_seconds": 60,
            "auth": {"type": "bearer", "token": "abc"},
            "retry": {"max_attempts": 3, "delay_ms": 1000}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_InvalidJson_ReturnsError()
    {
        // Arrange
        var json = """{"url": "https://example.com" invalid}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("Invalid JSON", result.Errors[0]);
    }

    [Fact]
    public void Validate_MissingUrl_ReturnsError()
    {
        // Arrange
        var json = """{"method": "GET"}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_EmptyUrl_ReturnsError()
    {
        // Arrange
        var json = """{"url": ""}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_InvalidUrlFormat_ReturnsError()
    {
        // Arrange
        var json = """{"url": "not-a-valid-url"}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Invalid URL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_InvalidUrlScheme_ReturnsError()
    {
        // Arrange
        var json = """{"url": "ftp://example.com/file"}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_InvalidMethod_ReturnsError()
    {
        // Arrange
        var json = """{"url": "https://example.com", "method": "INVALID"}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("method", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void Validate_ValidMethods_ReturnsSuccess(string method)
    {
        // Arrange
        var json = $$"""{"url": "https://example.com", "method": "{{method}}"}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_TimeoutTooLow_ReturnsError()
    {
        // Arrange
        var json = """{"url": "https://example.com", "timeout_seconds": 0}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_TimeoutTooHigh_ReturnsError()
    {
        // Arrange
        var json = """{"url": "https://example.com", "timeout_seconds": 500}""";

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_BasicAuthMissingUsername_ReturnsError()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "auth": {"type": "basic", "password": "secret"}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("username", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_BearerAuthMissingToken_ReturnsError()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "auth": {"type": "bearer"}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ApiKeyAuthMissingKey_ReturnsError()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "auth": {"type": "api_key", "key_header": "X-API-Key"}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ApiKeyAuthMissingHeaderOrQueryParam_ReturnsError()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "auth": {"type": "api_key", "key": "my-key"}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("key_header", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_UnknownAuthType_ReturnsError()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "auth": {"type": "oauth2"}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Unknown auth type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RetryMaxAttemptsTooLow_ReturnsError()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "retry": {"max_attempts": 0}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("max_attempts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RetryMaxAttemptsTooHigh_ReturnsError()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "retry": {"max_attempts": 15}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("max_attempts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RetryDelayTooLow_ReturnsError()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "retry": {"delay_ms": 50}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("delay_ms", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_BothBodyAndBodyRaw_ReturnsWarning()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "body": {"key": "value"},
            "body_raw": "raw content"
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("body", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_EmptyRetryStatusCodes_ReturnsWarning()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "retry": {"retry_status_codes": []}
        }
        """;

        // Act
        var result = RequestValidator.Validate(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
    }
}
