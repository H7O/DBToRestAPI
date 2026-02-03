using System.Text.Json;
using DBToRestAPI.Services.HttpExecutor.Internal;
using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Unit tests for HTTP request message building functionality.
/// </summary>
public class RequestMessageBuilderTests
{
    [Fact]
    public void Build_MinimalRequest_CreatesValidMessage()
    {
        // Arrange
        var request = new HttpExecutorRequest { Url = "https://example.com/api" };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.Equal(HttpMethod.Get, message.Method);
        Assert.Equal("https://example.com/api", message.RequestUri?.ToString());
        Assert.Null(message.Content);
    }

    [Fact]
    public void Build_PostRequest_SetsCorrectMethod()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Method = "POST"
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.Equal(HttpMethod.Post, message.Method);
    }

    [Fact]
    public void Build_WithQueryParameters_AppendsToUrl()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Query = new Dictionary<string, string>
            {
                ["page"] = "1",
                ["limit"] = "50"
            }
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        var url = message.RequestUri?.ToString();
        Assert.NotNull(url);
        Assert.Contains("page=1", url);
        Assert.Contains("limit=50", url);
    }

    [Fact]
    public void Build_WithJsonBody_SerializesAndSetsContent()
    {
        // Arrange
        var body = new { name = "John", age = 30 };
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Method = "POST",
            Body = body
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.NotNull(message.Content);
        var contentType = message.Content.Headers.ContentType;
        Assert.NotNull(contentType);
        Assert.Equal("application/json", contentType.MediaType);
    }

    [Fact]
    public async Task Build_WithJsonBody_ContentIsCorrect()
    {
        // Arrange
        var body = new { name = "John", age = 30 };
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Method = "POST",
            Body = body
        };

        // Act
        var message = RequestMessageBuilder.Build(request);
        var content = await message.Content!.ReadAsStringAsync();

        // Assert
        Assert.Contains("\"name\"", content);
        Assert.Contains("John", content);
        Assert.Contains("\"age\"", content);
        Assert.Contains("30", content);
    }

    [Fact]
    public async Task Build_WithRawBody_SetsContentDirectly()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Method = "POST",
            ContentType = "application/xml",
            BodyRaw = "<request><id>123</id></request>"
        };

        // Act
        var message = RequestMessageBuilder.Build(request);
        var content = await message.Content!.ReadAsStringAsync();

        // Assert
        Assert.Equal("<request><id>123</id></request>", content);
        Assert.Equal("application/xml", message.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Build_WithJsonElement_SerializesCorrectly()
    {
        // Arrange
        var jsonString = """{"name": "John", "age": 30}""";
        var jsonElement = JsonSerializer.Deserialize<JsonElement>(jsonString);
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Method = "POST",
            Body = jsonElement
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.NotNull(message.Content);
    }

    [Fact]
    public void Build_WithHeaders_AddsToRequest()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Headers = new Dictionary<string, string>
            {
                ["X-Custom-Header"] = "custom-value",
                ["X-Request-Id"] = "abc-123"
            }
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.True(message.Headers.TryGetValues("X-Custom-Header", out var values1));
        Assert.Equal("custom-value", values1.First());

        Assert.True(message.Headers.TryGetValues("X-Request-Id", out var values2));
        Assert.Equal("abc-123", values2.First());
    }

    [Fact]
    public void Build_WithBearerAuth_SetsAuthorizationHeader()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Auth = new HttpExecutorAuth
            {
                Type = "bearer",
                Token = "my-token"
            }
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.NotNull(message.Headers.Authorization);
        Assert.Equal("Bearer", message.Headers.Authorization.Scheme);
        Assert.Equal("my-token", message.Headers.Authorization.Parameter);
    }

    [Fact]
    public void Build_WithBasicAuth_SetsAuthorizationHeader()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Auth = new HttpExecutorAuth
            {
                Type = "basic",
                Username = "admin",
                Password = "secret"
            }
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.NotNull(message.Headers.Authorization);
        Assert.Equal("Basic", message.Headers.Authorization.Scheme);
    }

    [Fact]
    public void Build_WithApiKeyInHeader_SetsCustomHeader()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Auth = new HttpExecutorAuth
            {
                Type = "api_key",
                Key = "sk-abc123",
                KeyHeader = "X-API-Key"
            }
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.True(message.Headers.TryGetValues("X-API-Key", out var values));
        Assert.Equal("sk-abc123", values.First());
    }

    [Fact]
    public void Build_WithApiKeyInQueryString_ModifiesUrl()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Auth = new HttpExecutorAuth
            {
                Type = "api_key",
                Key = "sk-abc123",
                KeyQueryParam = "api_key"
            }
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        var url = message.RequestUri?.ToString();
        Assert.NotNull(url);
        Assert.Contains("api_key=sk-abc123", url);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void Build_AllHttpMethods_CreatesValidMessage(string method)
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Method = method
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.Equal(new HttpMethod(method), message.Method);
    }

    [Fact]
    public void Build_BodyTakesPrecedenceOverBodyRaw()
    {
        // Arrange
        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Method = "POST",
            Body = new { key = "value" },
            BodyRaw = "raw content"
        };

        // Act
        var message = RequestMessageBuilder.Build(request);

        // Assert
        Assert.NotNull(message.Content);
        // Content should be JSON from Body, not raw
        var contentType = message.Content.Headers.ContentType;
        Assert.Equal("application/json", contentType?.MediaType);
    }
}
