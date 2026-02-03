using DBToRestAPI.Services.HttpExecutor.Internal;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Unit tests for query string building functionality.
/// </summary>
public class QueryStringBuilderTests
{
    [Fact]
    public void AppendQueryParameters_NullParams_ReturnsOriginalUrl()
    {
        // Arrange
        var url = "https://example.com/api";

        // Act
        var result = QueryStringBuilder.AppendQueryParameters(url, null);

        // Assert
        Assert.Equal(url, result);
    }

    [Fact]
    public void AppendQueryParameters_EmptyParams_ReturnsOriginalUrl()
    {
        // Arrange
        var url = "https://example.com/api";
        var query = new Dictionary<string, string>();

        // Act
        var result = QueryStringBuilder.AppendQueryParameters(url, query);

        // Assert
        Assert.Equal(url, result);
    }

    [Fact]
    public void AppendQueryParameters_SingleParam_AppendsWithQuestionMark()
    {
        // Arrange
        var url = "https://example.com/api";
        var query = new Dictionary<string, string> { ["page"] = "1" };

        // Act
        var result = QueryStringBuilder.AppendQueryParameters(url, query);

        // Assert
        Assert.Equal("https://example.com/api?page=1", result);
    }

    [Fact]
    public void AppendQueryParameters_MultipleParams_AppendsAll()
    {
        // Arrange
        var url = "https://example.com/api";
        var query = new Dictionary<string, string>
        {
            ["page"] = "1",
            ["limit"] = "50"
        };

        // Act
        var result = QueryStringBuilder.AppendQueryParameters(url, query);

        // Assert
        Assert.Contains("page=1", result);
        Assert.Contains("limit=50", result);
        Assert.Contains("&", result);
    }

    [Fact]
    public void AppendQueryParameters_UrlAlreadyHasQueryString_AppendsWithAmpersand()
    {
        // Arrange
        var url = "https://example.com/api?existing=value";
        var query = new Dictionary<string, string> { ["page"] = "1" };

        // Act
        var result = QueryStringBuilder.AppendQueryParameters(url, query);

        // Assert
        Assert.Equal("https://example.com/api?existing=value&page=1", result);
    }

    [Fact]
    public void AppendQueryParameters_SpecialCharacters_UrlEncodes()
    {
        // Arrange
        var url = "https://example.com/search";
        var query = new Dictionary<string, string> { ["q"] = "hello world" };

        // Act
        var result = QueryStringBuilder.AppendQueryParameters(url, query);

        // Assert
        Assert.Contains("q=hello+world", result);
    }

    [Fact]
    public void AppendQueryParameters_SpecialCharactersInKey_UrlEncodes()
    {
        // Arrange
        var url = "https://example.com/api";
        var query = new Dictionary<string, string> { ["filter[name]"] = "test" };

        // Act
        var result = QueryStringBuilder.AppendQueryParameters(url, query);

        // Assert
        Assert.Contains("filter%5bname%5d=test", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendQueryParameter_SingleParam_AppendsCorrectly()
    {
        // Arrange
        var url = "https://example.com/api";

        // Act
        var result = QueryStringBuilder.AppendQueryParameter(url, "key", "value");

        // Assert
        Assert.Equal("https://example.com/api?key=value", result);
    }

    [Fact]
    public void AppendQueryParameter_UrlHasExistingQuery_AppendsWithAmpersand()
    {
        // Arrange
        var url = "https://example.com/api?existing=1";

        // Act
        var result = QueryStringBuilder.AppendQueryParameter(url, "key", "value");

        // Assert
        Assert.Equal("https://example.com/api?existing=1&key=value", result);
    }
}
