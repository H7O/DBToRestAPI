using DBToRestAPI.Controllers;

namespace DBToRestAPI.Tests;

/// <summary>
/// Tests for the skip-property detection logic in embedded HTTP calls.
/// ApiController.ShouldSkipHttpCall is an internal static method that parses
/// the "skip" property from the {http{...}http} JSON configuration.
/// </summary>
public class SkipPropertyTests
{
    #region Truthy values — should skip

    [Fact]
    public void ShouldSkip_BooleanTrue_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": true}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_StringTrue_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": "true"}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_StringTrueUpperCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": "TRUE"}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_StringTrueMixedCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": "True"}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_String1_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": "1"}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_StringYes_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": "yes"}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_StringYesUpperCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": "YES"}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_NumberOne_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": 1}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_NumberNonZero_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": 42}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_NegativeNumber_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "skip": -1}""";
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    #endregion

    #region Falsy values — should NOT skip

    [Fact]
    public void ShouldSkip_BooleanFalse_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": false}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_StringFalse_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": "false"}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_String0_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": "0"}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_StringNo_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": "no"}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_NumberZero_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": 0}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_EmptyString_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": ""}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_NullValue_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": null}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_RandomString_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": "maybe"}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    #endregion

    #region Absent property and edge cases

    [Fact]
    public void ShouldSkip_PropertyAbsent_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "method": "GET"}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_MinimalJson_ReturnsFalse()
    {
        var json = """{"url": "https://example.com"}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_InvalidJson_ReturnsFalse()
    {
        var json = "not valid json at all";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_EmptyObject_ReturnsFalse()
    {
        var json = "{}";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_ObjectValue_ReturnsFalse()
    {
        // skip is an object — not a recognized truthy type
        var json = """{"url": "https://example.com", "skip": {"value": true}}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void ShouldSkip_ArrayValue_ReturnsFalse()
    {
        // skip is an array — not a recognized truthy type
        var json = """{"url": "https://example.com", "skip": [true]}""";
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    #endregion
}
