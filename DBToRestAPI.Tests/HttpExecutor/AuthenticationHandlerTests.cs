using DBToRestAPI.Services.HttpExecutor.Internal;
using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Unit tests for authentication handling functionality.
/// </summary>
public class AuthenticationHandlerTests
{
    [Fact]
    public void ApplyAuthentication_NullAuth_ReturnsOriginalUrl()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var url = "https://example.com";

        // Act
        var result = AuthenticationHandler.ApplyAuthentication(request, null, url);

        // Assert
        Assert.Equal(url, result);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void ApplyAuthentication_BasicAuth_SetsAuthorizationHeader()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var auth = new HttpExecutorAuth
        {
            Type = "basic",
            Username = "admin",
            Password = "secret"
        };

        // Act
        AuthenticationHandler.ApplyAuthentication(request, auth, "https://example.com");

        // Assert
        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Basic", request.Headers.Authorization.Scheme);

        // Decode and verify
        var encodedCredentials = request.Headers.Authorization.Parameter;
        Assert.NotNull(encodedCredentials);
        var decodedBytes = Convert.FromBase64String(encodedCredentials);
        var decoded = System.Text.Encoding.UTF8.GetString(decodedBytes);
        Assert.Equal("admin:secret", decoded);
    }

    [Fact]
    public void ApplyAuthentication_BasicAuthNoPassword_UsesEmptyPassword()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var auth = new HttpExecutorAuth
        {
            Type = "basic",
            Username = "admin"
        };

        // Act
        AuthenticationHandler.ApplyAuthentication(request, auth, "https://example.com");

        // Assert
        var encodedCredentials = request.Headers.Authorization?.Parameter;
        Assert.NotNull(encodedCredentials);
        var decodedBytes = Convert.FromBase64String(encodedCredentials);
        var decoded = System.Text.Encoding.UTF8.GetString(decodedBytes);
        Assert.Equal("admin:", decoded);
    }

    [Fact]
    public void ApplyAuthentication_BearerAuth_SetsAuthorizationHeader()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var auth = new HttpExecutorAuth
        {
            Type = "bearer",
            Token = "my-jwt-token"
        };

        // Act
        AuthenticationHandler.ApplyAuthentication(request, auth, "https://example.com");

        // Assert
        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
        Assert.Equal("my-jwt-token", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public void ApplyAuthentication_BearerAuthEmptyToken_DoesNotSetHeader()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var auth = new HttpExecutorAuth
        {
            Type = "bearer",
            Token = ""
        };

        // Act
        AuthenticationHandler.ApplyAuthentication(request, auth, "https://example.com");

        // Assert
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void ApplyAuthentication_ApiKeyInHeader_SetsCustomHeader()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var auth = new HttpExecutorAuth
        {
            Type = "api_key",
            Key = "sk-abc123",
            KeyHeader = "X-API-Key"
        };

        // Act
        AuthenticationHandler.ApplyAuthentication(request, auth, "https://example.com");

        // Assert
        Assert.True(request.Headers.TryGetValues("X-API-Key", out var values));
        Assert.Equal("sk-abc123", values.First());
    }

    [Fact]
    public void ApplyAuthentication_ApiKeyInQueryString_ModifiesUrl()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
        var auth = new HttpExecutorAuth
        {
            Type = "api_key",
            Key = "sk-abc123",
            KeyQueryParam = "api_key"
        };

        // Act
        var result = AuthenticationHandler.ApplyAuthentication(request, auth, "https://example.com/api");

        // Assert
        Assert.Contains("api_key=sk-abc123", result);
    }

    [Fact]
    public void ApplyAuthentication_ApiKeyInQueryString_UrlAlreadyHasParams_AppendsWithAmpersand()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api?page=1");
        var auth = new HttpExecutorAuth
        {
            Type = "api_key",
            Key = "sk-abc123",
            KeyQueryParam = "api_key"
        };

        // Act
        var result = AuthenticationHandler.ApplyAuthentication(request, auth, "https://example.com/api?page=1");

        // Assert
        Assert.Contains("?page=1", result);
        Assert.Contains("&api_key=sk-abc123", result);
    }

    [Fact]
    public void ApplyAuthentication_UnknownAuthType_ReturnsOriginalUrl()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var auth = new HttpExecutorAuth { Type = "unknown" };

        // Act
        var result = AuthenticationHandler.ApplyAuthentication(request, auth, "https://example.com");

        // Assert
        Assert.Equal("https://example.com", result);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void Validate_ValidBasicAuth_ReturnsNoErrors()
    {
        // Arrange
        var auth = new HttpExecutorAuth
        {
            Type = "basic",
            Username = "admin",
            Password = "secret"
        };

        // Act
        var errors = AuthenticationHandler.Validate(auth);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_BasicAuthMissingUsername_ReturnsError()
    {
        // Arrange
        var auth = new HttpExecutorAuth
        {
            Type = "basic",
            Password = "secret"
        };

        // Act
        var errors = AuthenticationHandler.Validate(auth);

        // Assert
        Assert.Single(errors);
        Assert.Contains("username", errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ValidBearerAuth_ReturnsNoErrors()
    {
        // Arrange
        var auth = new HttpExecutorAuth
        {
            Type = "bearer",
            Token = "my-token"
        };

        // Act
        var errors = AuthenticationHandler.Validate(auth);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_BearerAuthMissingToken_ReturnsError()
    {
        // Arrange
        var auth = new HttpExecutorAuth { Type = "bearer" };

        // Act
        var errors = AuthenticationHandler.Validate(auth);

        // Assert
        Assert.Single(errors);
        Assert.Contains("token", errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ValidApiKeyWithHeader_ReturnsNoErrors()
    {
        // Arrange
        var auth = new HttpExecutorAuth
        {
            Type = "api_key",
            Key = "my-key",
            KeyHeader = "X-API-Key"
        };

        // Act
        var errors = AuthenticationHandler.Validate(auth);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ValidApiKeyWithQueryParam_ReturnsNoErrors()
    {
        // Arrange
        var auth = new HttpExecutorAuth
        {
            Type = "api_key",
            Key = "my-key",
            KeyQueryParam = "api_key"
        };

        // Act
        var errors = AuthenticationHandler.Validate(auth);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ApiKeyMissingKeyAndHeaderOrParam_ReturnsErrors()
    {
        // Arrange
        var auth = new HttpExecutorAuth { Type = "api_key" };

        // Act
        var errors = AuthenticationHandler.Validate(auth);

        // Assert
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Validate_UnknownAuthType_ReturnsError()
    {
        // Arrange
        var auth = new HttpExecutorAuth { Type = "oauth2" };

        // Act
        var errors = AuthenticationHandler.Validate(auth);

        // Assert
        Assert.Single(errors);
        Assert.Contains("Unknown auth type", errors[0]);
    }
}
