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

    #region ToStructuredJson tests

    [Fact]
    public void ToStructuredJson_SuccessWithJsonContent_ReturnsStructuredJsonWithParsedData()
    {
        // Arrange
        var json = """{"name": "John", "age": 30}""";
        var content = System.Text.Encoding.UTF8.GetBytes(json);
        var response = new HttpExecutorResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Content = content,
            Headers = new Dictionary<string, string[]>
            {
                ["X-Request-Id"] = ["abc-123"]
            },
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"]
            }
        };

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal(200, root.GetProperty("status_code").GetInt32());
        Assert.Equal("abc-123", root.GetProperty("headers").GetProperty("X-Request-Id").GetString());
        Assert.Equal("application/json", root.GetProperty("headers").GetProperty("Content-Type").GetString());
        // data should be a parsed JSON object, not a string
        Assert.Equal(System.Text.Json.JsonValueKind.Object, root.GetProperty("data").ValueKind);
        Assert.Equal("John", root.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal(30, root.GetProperty("data").GetProperty("age").GetInt32());
        // error should be null for successful responses
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public void ToStructuredJson_SuccessWithJsonArray_ReturnsDataAsArray()
    {
        // Arrange
        var json = """[{"id": 1}, {"id": 2}]""";
        var content = System.Text.Encoding.UTF8.GetBytes(json);
        var response = new HttpExecutorResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Content = content,
            Headers = [],
            ContentHeaders = []
        };

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal(System.Text.Json.JsonValueKind.Array, root.GetProperty("data").ValueKind);
        Assert.Equal(2, root.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public void ToStructuredJson_SuccessWithPlainTextContent_ReturnsDataAsString()
    {
        // Arrange
        var content = System.Text.Encoding.UTF8.GetBytes("Hello, plain text!");
        var response = new HttpExecutorResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Content = content,
            Headers = [],
            ContentHeaders = []
        };

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal(200, root.GetProperty("status_code").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.String, root.GetProperty("data").ValueKind);
        Assert.Equal("Hello, plain text!", root.GetProperty("data").GetString());
    }

    [Fact]
    public void ToStructuredJson_ErrorWithNoContent_ReturnsDataAsNull()
    {
        // Arrange
        var response = HttpExecutorResponse.FromError(
            "Connection refused", null, TimeSpan.FromMilliseconds(100), 0);

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal(0, root.GetProperty("status_code").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, root.GetProperty("headers").ValueKind);
        // error should be populated for status_code=0
        Assert.Equal(System.Text.Json.JsonValueKind.Object, root.GetProperty("error").ValueKind);
        Assert.Equal("Connection refused", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void ToStructuredJson_404WithErrorBody_ReturnsStatusAndData()
    {
        // Arrange
        var errorJson = """{"error": "Resource not found", "code": "NOT_FOUND"}""";
        var content = System.Text.Encoding.UTF8.GetBytes(errorJson);
        var response = new HttpExecutorResponse
        {
            IsSuccess = false,
            StatusCode = 404,
            Content = content,
            Headers = [],
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json; charset=utf-8"]
            }
        };

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal(404, root.GetProperty("status_code").GetInt32());
        Assert.Equal("Resource not found", root.GetProperty("data").GetProperty("error").GetString());
        Assert.Equal("NOT_FOUND", root.GetProperty("data").GetProperty("code").GetString());
        // error should be null — server responded (even though 404)
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public void ToStructuredJson_MultiValueHeaders_JoinedWithComma()
    {
        // Arrange
        var response = new HttpExecutorResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Content = System.Text.Encoding.UTF8.GetBytes("{}"),
            Headers = new Dictionary<string, string[]>
            {
                ["Set-Cookie"] = ["session=abc", "theme=dark"]
            },
            ContentHeaders = []
        };

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal("session=abc, theme=dark", root.GetProperty("headers").GetProperty("Set-Cookie").GetString());
    }

    [Fact]
    public void ToStructuredJson_EmptyContent_ReturnsNullData()
    {
        // Arrange — 204 No Content scenario
        var response = new HttpExecutorResponse
        {
            IsSuccess = true,
            StatusCode = 204,
            Content = [],
            Headers = [],
            ContentHeaders = []
        };

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal(204, root.GetProperty("status_code").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("data").ValueKind);
    }

    [Fact]
    public void ToStructuredJson_MergesResponseAndContentHeaders()
    {
        // Arrange
        var response = new HttpExecutorResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Content = System.Text.Encoding.UTF8.GetBytes("\"ok\""),
            Headers = new Dictionary<string, string[]>
            {
                ["X-Custom"] = ["value1"]
            },
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["text/plain"],
                ["Content-Length"] = ["4"]
            }
        };

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var headers = doc.RootElement.GetProperty("headers");

        // Assert — all 3 headers present
        Assert.Equal("value1", headers.GetProperty("X-Custom").GetString());
        Assert.Equal("text/plain", headers.GetProperty("Content-Type").GetString());
        Assert.Equal("4", headers.GetProperty("Content-Length").GetString());
    }

    [Fact]
    public void ToStructuredJson_InfrastructureError_HasErrorWithMessage()
    {
        // Arrange — simulate a timeout
        var response = HttpExecutorResponse.FromError(
            "Request timed out after 30 seconds",
            new TaskCanceledException(),
            TimeSpan.FromSeconds(30),
            2);

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal(0, root.GetProperty("status_code").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("data").ValueKind);
        var error = root.GetProperty("error");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, error.ValueKind);
        Assert.Equal("Request timed out after 30 seconds", error.GetProperty("message").GetString());
    }

    [Fact]
    public void ToStructuredJson_DnsError_HasErrorWithMessage()
    {
        // Arrange — simulate a DNS failure
        var response = HttpExecutorResponse.FromError(
            "Host not found: api.example.com",
            new HttpRequestException("No such host is known."),
            TimeSpan.FromMilliseconds(50),
            0);

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert
        Assert.Equal(0, root.GetProperty("status_code").GetInt32());
        Assert.Equal("Host not found: api.example.com", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void ToStructuredJson_ServerError500_ErrorIsNullNotPopulated()
    {
        // Arrange — 500 from server (server responded, so error should be null)
        var content = System.Text.Encoding.UTF8.GetBytes("""{"message": "Internal Server Error"}""");
        var response = new HttpExecutorResponse
        {
            IsSuccess = false,
            StatusCode = 500,
            ErrorMessage = "Server error",  // even though ErrorMessage is set
            Content = content,
            Headers = [],
            ContentHeaders = []
        };

        // Act
        var result = response.ToStructuredJson();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Assert — error must be null because server responded (status_code != 0)
        Assert.Equal(500, root.GetProperty("status_code").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("error").ValueKind);
        // Server's error details are in data
        Assert.Equal("Internal Server Error", root.GetProperty("data").GetProperty("message").GetString());
    }

    #endregion

    private class TestPerson
    {
        public string? Name { get; set; }
        public int Age { get; set; }
    }
}
