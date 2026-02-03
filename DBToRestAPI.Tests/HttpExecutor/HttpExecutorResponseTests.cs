using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Unit tests for HttpExecutorResponse functionality.
/// </summary>
public class HttpExecutorResponseTests
{
    [Fact]
    public void ContentAsString_ReturnsUtf8DecodedString()
    {
        // Arrange
        var content = System.Text.Encoding.UTF8.GetBytes("Hello, World!");
        var response = new HttpExecutorResponse { Content = content };

        // Act
        var result = response.ContentAsString;

        // Assert
        Assert.Equal("Hello, World!", result);
    }

    [Fact]
    public void ContentAsString_EmptyContent_ReturnsEmptyString()
    {
        // Arrange
        var response = new HttpExecutorResponse { Content = [] };

        // Act
        var result = response.ContentAsString;

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ContentAs_ValidJson_DeserializesCorrectly()
    {
        // Arrange
        var json = """{"name": "John", "age": 30}""";
        var content = System.Text.Encoding.UTF8.GetBytes(json);
        var response = new HttpExecutorResponse { Content = content };

        // Act
        var result = response.ContentAs<TestPerson>();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("John", result.Name);
        Assert.Equal(30, result.Age);
    }

    [Fact]
    public void ContentAs_InvalidJson_ReturnsNull()
    {
        // Arrange
        var content = System.Text.Encoding.UTF8.GetBytes("not valid json");
        var response = new HttpExecutorResponse { Content = content };

        // Act
        var result = response.ContentAs<TestPerson>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ContentAs_EmptyContent_ReturnsNull()
    {
        // Arrange
        var response = new HttpExecutorResponse { Content = [] };

        // Act
        var result = response.ContentAs<TestPerson>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ContentAsJson_ValidJson_ReturnsJsonElement()
    {
        // Arrange
        var json = """{"name": "John", "age": 30}""";
        var content = System.Text.Encoding.UTF8.GetBytes(json);
        var response = new HttpExecutorResponse { Content = content };

        // Act
        var result = response.ContentAsJson();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("John", result.Value.GetProperty("name").GetString());
        Assert.Equal(30, result.Value.GetProperty("age").GetInt32());
    }

    [Fact]
    public void ContentAsJson_InvalidJson_ReturnsNull()
    {
        // Arrange
        var content = System.Text.Encoding.UTF8.GetBytes("not valid json");
        var response = new HttpExecutorResponse { Content = content };

        // Act
        var result = response.ContentAsJson();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ContentAsJson_EmptyContent_ReturnsNull()
    {
        // Arrange
        var response = new HttpExecutorResponse { Content = [] };

        // Act
        var result = response.ContentAsJson();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FromHttpResponseAsync_Success_CapturesAllData()
    {
        // Arrange
        var httpResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            ReasonPhrase = "OK",
            Content = new StringContent("""{"result": "success"}""", System.Text.Encoding.UTF8, "application/json")
        };
        httpResponse.Headers.Add("X-Custom-Header", "value");
        var elapsed = TimeSpan.FromMilliseconds(150);

        // Act
        var response = await HttpExecutorResponse.FromHttpResponseAsync(
            httpResponse, elapsed, 0, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("OK", response.ReasonPhrase);
        Assert.Equal(elapsed, response.ElapsedTime);
        Assert.Equal(0, response.RetryAttempts);
        Assert.Contains("X-Custom-Header", response.Headers.Keys);
        Assert.Contains("Content-Type", response.ContentHeaders.Keys);
        Assert.Contains("success", response.ContentAsString);
    }

    [Fact]
    public async Task FromHttpResponseAsync_Error_CapturesStatusCode()
    {
        // Arrange
        var httpResponse = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            ReasonPhrase = "Not Found",
            Content = new StringContent("Resource not found")
        };

        // Act
        var response = await HttpExecutorResponse.FromHttpResponseAsync(
            httpResponse, TimeSpan.Zero, 2, CancellationToken.None);

        // Assert
        Assert.False(response.IsSuccess);
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Not Found", response.ReasonPhrase);
        Assert.Equal(2, response.RetryAttempts);
    }

    [Fact]
    public void FromError_CreatesErrorResponse()
    {
        // Arrange
        var exception = new HttpRequestException("Connection failed");
        var elapsed = TimeSpan.FromMilliseconds(500);

        // Act
        var response = HttpExecutorResponse.FromError(
            "Connection failed", exception, elapsed, 3);

        // Assert
        Assert.False(response.IsSuccess);
        Assert.Equal(0, response.StatusCode);
        Assert.Equal("Connection failed", response.ErrorMessage);
        Assert.Same(exception, response.Exception);
        Assert.Equal(elapsed, response.ElapsedTime);
        Assert.Equal(3, response.RetryAttempts);
    }

    [Fact]
    public void FromError_WithStatusCode_SetsStatusCode()
    {
        // Arrange
        var elapsed = TimeSpan.FromMilliseconds(100);

        // Act
        var response = HttpExecutorResponse.FromError(
            "Bad Request", null, elapsed, 0, statusCode: 400);

        // Assert
        Assert.Equal(400, response.StatusCode);
    }

    private class TestPerson
    {
        public string? Name { get; set; }
        public int Age { get; set; }
    }
}
