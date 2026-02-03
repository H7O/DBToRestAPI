using System.Net;
using DBToRestAPI.Services.HttpExecutor;
using DBToRestAPI.Services.HttpExecutor.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Integration tests for the HttpRequestExecutor service.
/// Uses a mock HTTP handler to simulate HTTP responses.
/// </summary>
public class HttpRequestExecutorTests
{
    private readonly Mock<ILogger<HttpRequestExecutor>> _loggerMock;
    private readonly HttpExecutorOptions _options;

    public HttpRequestExecutorTests()
    {
        _loggerMock = new Mock<ILogger<HttpRequestExecutor>>();
        _options = new HttpExecutorOptions
        {
            DefaultTimeoutSeconds = 30,
            EnableRequestLogging = false
        };
    }

    private HttpRequestExecutor CreateExecutor(MockHttpMessageHandler handler)
    {
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();

        // Create client with mock handler - use disposeHandler: false to prevent disposal issues
        httpClientFactoryMock
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new HttpRequestExecutor(httpClientFactoryMock.Object, _options, _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulGet_ReturnsSuccessResponse()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"message": "success"}""")
        }));
        var executor = CreateExecutor(handler);

        // Act
        var response = await executor.ExecuteAsync("""{"url": "https://example.com/api"}""");

        // Assert
        Assert.True(response.IsSuccess);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.ContentAsString);
    }

    [Fact]
    public async Task ExecuteAsync_NotFound_ReturnsFailureResponse()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            ReasonPhrase = "Not Found",
            Content = new StringContent("Resource not found")
        }));
        var executor = CreateExecutor(handler);

        // Act
        var response = await executor.ExecuteAsync("""{"url": "https://example.com/api/missing"}""");

        // Assert
        Assert.False(response.IsSuccess);
        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteAsync_ServerError_ReturnsFailureResponse()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            ReasonPhrase = "Internal Server Error"
        }));
        var executor = CreateExecutor(handler);

        // Act
        var response = await executor.ExecuteAsync("""{"url": "https://example.com/api"}""");

        // Assert
        Assert.False(response.IsSuccess);
        Assert.Equal(500, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsErrorResponse()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var executor = CreateExecutor(handler);

        // Act
        var response = await executor.ExecuteAsync("not valid json");

        // Assert
        Assert.False(response.IsSuccess);
        Assert.Equal(0, response.StatusCode);
        Assert.Contains("Invalid JSON", response.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_MissingUrl_ReturnsErrorResponse()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var executor = CreateExecutor(handler);

        // Act
        var response = await executor.ExecuteAsync("""{"method": "GET"}""");

        // Assert
        Assert.False(response.IsSuccess);
        Assert.Equal(0, response.StatusCode);
        Assert.Contains("url", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithHeaders_SendsHeaders()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var executor = CreateExecutor(handler);

        // Act
        await executor.ExecuteAsync("""
        {
            "url": "https://example.com/api",
            "headers": {
                "X-Custom-Header": "custom-value"
            }
        }
        """);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.TryGetValues("X-Custom-Header", out var values));
        Assert.Equal("custom-value", values.First());
    }

    [Fact]
    public async Task ExecuteAsync_WithBearerAuth_SendsAuthorizationHeader()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var executor = CreateExecutor(handler);

        // Act
        await executor.ExecuteAsync("""
        {
            "url": "https://example.com/api",
            "auth": {
                "type": "bearer",
                "token": "my-token"
            }
        }
        """);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("my-token", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ExecuteAsync_WithQueryParams_AppendsToUrl()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var executor = CreateExecutor(handler);

        // Act
        await executor.ExecuteAsync("""
        {
            "url": "https://example.com/api",
            "query": {
                "page": "1",
                "limit": "50"
            }
        }
        """);

        // Assert
        Assert.NotNull(capturedRequest);
        var url = capturedRequest.RequestUri?.ToString();
        Assert.Contains("page=1", url);
        Assert.Contains("limit=50", url);
    }

    [Fact]
    public async Task ExecuteAsync_PostWithBody_SendsJsonContent()
    {
        // Arrange
        HttpMethod? capturedMethod = null;
        string? capturedContent = null;
        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            capturedMethod = request.Method;
            if (request.Content != null)
                capturedContent = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var executor = CreateExecutor(handler);

        // Act
        await executor.ExecuteAsync("""
        {
            "url": "https://example.com/api/users",
            "method": "POST",
            "body": {
                "name": "John",
                "email": "john@example.com"
            }
        }
        """);

        // Assert
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.NotNull(capturedContent);
        Assert.Contains("John", capturedContent);
        Assert.Contains("john@example.com", capturedContent);
    }

    [Fact]
    public async Task ExecuteAsync_StronglyTypedRequest_Works()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id": 123}""")
        }));
        var executor = CreateExecutor(handler);

        var request = new HttpExecutorRequest
        {
            Url = "https://example.com/api",
            Method = "GET"
        };

        // Act
        var response = await executor.ExecuteAsync(request);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public void Validate_ValidRequest_ReturnsSuccess()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var executor = CreateExecutor(handler);

        // Act
        var result = executor.Validate("""{"url": "https://example.com/api"}""");

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_InvalidRequest_ReturnsErrors()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var executor = CreateExecutor(handler);

        // Act
        var result = executor.Validate("""{"method": "GET"}""");

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ExecuteAsync_TracksElapsedTime()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            await Task.Delay(50, ct); // Small delay
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var executor = CreateExecutor(handler);

        // Act
        var response = await executor.ExecuteAsync("""{"url": "https://example.com/api"}""");

        // Assert
        Assert.True(response.IsSuccess);
        Assert.True(response.ElapsedTime.TotalMilliseconds >= 40); // Allow some tolerance
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_ReturnsErrorResponse()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            await Task.Delay(5000, ct); // Long delay
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var executor = CreateExecutor(handler);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        // Act
        var response = await executor.ExecuteAsync(
            """{"url": "https://example.com/api"}""",
            cts.Token);

        // Assert
        Assert.False(response.IsSuccess);
        Assert.Contains("cancelled", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionError_ReturnsErrorResponse()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            throw new HttpRequestException("Connection refused");
        });
        var executor = CreateExecutor(handler);

        // Act
        var response = await executor.ExecuteAsync("""{"url": "https://example.com/api"}""");

        // Assert
        Assert.False(response.IsSuccess);
        Assert.Equal(0, response.StatusCode);
        Assert.NotNull(response.ErrorMessage);
        Assert.NotNull(response.Exception);
    }

    /// <summary>
    /// Mock HTTP message handler for testing.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
