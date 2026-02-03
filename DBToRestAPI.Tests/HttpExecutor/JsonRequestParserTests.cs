using DBToRestAPI.Services.HttpExecutor.Internal;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Unit tests for JSON request parsing functionality.
/// </summary>
public class JsonRequestParserTests
{
    [Fact]
    public void Parse_MinimalRequest_SetsDefaults()
    {
        // Arrange
        var json = """{"url": "https://example.com/api"}""";

        // Act
        var request = JsonRequestParser.Parse(json);

        // Assert
        Assert.Equal("https://example.com/api", request.Url);
        Assert.Equal("GET", request.Method);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(30, request.TimeoutSeconds);
        Assert.False(request.IgnoreCertificateErrors);
        Assert.True(request.FollowRedirects);
        Assert.Null(request.Auth);
        Assert.Null(request.Retry);
        Assert.Null(request.Headers);
        Assert.Null(request.Query);
        Assert.Null(request.Body);
        Assert.Null(request.BodyRaw);
    }

    [Fact]
    public void Parse_FullRequest_ParsesAllFields()
    {
        // Arrange
        var json = """
        {
            "url": "https://api.example.com/users",
            "method": "POST",
            "headers": {
                "X-Custom-Header": "value",
                "X-Request-Id": "abc-123"
            },
            "query": {
                "page": "1",
                "limit": "50"
            },
            "body": {
                "name": "John Doe",
                "email": "john@example.com"
            },
            "content_type": "application/json",
            "timeout_seconds": 60,
            "ignore_certificate_errors": true,
            "follow_redirects": false,
            "auth": {
                "type": "bearer",
                "token": "my-token"
            },
            "retry": {
                "max_attempts": 5,
                "delay_ms": 2000,
                "exponential_backoff": false,
                "retry_status_codes": [500, 502, 503]
            }
        }
        """;

        // Act
        var request = JsonRequestParser.Parse(json);

        // Assert
        Assert.Equal("https://api.example.com/users", request.Url);
        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(60, request.TimeoutSeconds);
        Assert.True(request.IgnoreCertificateErrors);
        Assert.False(request.FollowRedirects);

        // Headers
        Assert.NotNull(request.Headers);
        Assert.Equal(2, request.Headers.Count);
        Assert.Equal("value", request.Headers["X-Custom-Header"]);
        Assert.Equal("abc-123", request.Headers["X-Request-Id"]);

        // Query
        Assert.NotNull(request.Query);
        Assert.Equal(2, request.Query.Count);
        Assert.Equal("1", request.Query["page"]);
        Assert.Equal("50", request.Query["limit"]);

        // Auth
        Assert.NotNull(request.Auth);
        Assert.Equal("bearer", request.Auth.Type);
        Assert.Equal("my-token", request.Auth.Token);

        // Retry
        Assert.NotNull(request.Retry);
        Assert.Equal(5, request.Retry.MaxAttempts);
        Assert.Equal(2000, request.Retry.DelayMs);
        Assert.False(request.Retry.ExponentialBackoff);
        Assert.NotNull(request.Retry.RetryStatusCodes);
        Assert.Equal([500, 502, 503], request.Retry.RetryStatusCodes);
    }

    [Fact]
    public void Parse_MissingUrl_ThrowsArgumentException()
    {
        // Arrange
        var json = """{"method": "GET"}""";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => JsonRequestParser.Parse(json));
        Assert.Contains("url", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_BasicAuth_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "auth": {
                "type": "basic",
                "username": "admin",
                "password": "secret123"
            }
        }
        """;

        // Act
        var request = JsonRequestParser.Parse(json);

        // Assert
        Assert.NotNull(request.Auth);
        Assert.Equal("basic", request.Auth.Type);
        Assert.Equal("admin", request.Auth.Username);
        Assert.Equal("secret123", request.Auth.Password);
    }

    [Fact]
    public void Parse_ApiKeyAuth_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "auth": {
                "type": "api_key",
                "key": "sk-abc123",
                "key_header": "X-API-Key"
            }
        }
        """;

        // Act
        var request = JsonRequestParser.Parse(json);

        // Assert
        Assert.NotNull(request.Auth);
        Assert.Equal("api_key", request.Auth.Type);
        Assert.Equal("sk-abc123", request.Auth.Key);
        Assert.Equal("X-API-Key", request.Auth.KeyHeader);
    }

    [Fact]
    public void Parse_RawBody_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "method": "POST",
            "content_type": "application/xml",
            "body_raw": "<request><id>123</id></request>"
        }
        """;

        // Act
        var request = JsonRequestParser.Parse(json);

        // Assert
        Assert.Equal("application/xml", request.ContentType);
        Assert.Equal("<request><id>123</id></request>", request.BodyRaw);
        Assert.Null(request.Body);
    }

    [Theory]
    [InlineData("get", "GET")]
    [InlineData("GET", "GET")]
    [InlineData("post", "POST")]
    [InlineData("Post", "POST")]
    [InlineData("DELETE", "DELETE")]
    public void Parse_MethodNormalization_ConvertsToUpperCase(string input, string expected)
    {
        // Arrange
        var json = $$"""{"url": "https://example.com", "method": "{{input}}"}""";

        // Act
        var request = JsonRequestParser.Parse(json);

        // Assert
        Assert.Equal(expected, request.Method);
    }

    [Fact]
    public void Parse_JsonWithComments_ParsesSuccessfully()
    {
        // Arrange
        var json = """
        {
            // This is a comment
            "url": "https://example.com",
            "method": "GET" // trailing comment
        }
        """;

        // Act
        var request = JsonRequestParser.Parse(json);

        // Assert
        Assert.Equal("https://example.com", request.Url);
    }

    [Fact]
    public void Parse_JsonWithTrailingCommas_ParsesSuccessfully()
    {
        // Arrange
        var json = """
        {
            "url": "https://example.com",
            "method": "GET",
        }
        """;

        // Act
        var request = JsonRequestParser.Parse(json);

        // Assert
        Assert.Equal("https://example.com", request.Url);
    }
}
