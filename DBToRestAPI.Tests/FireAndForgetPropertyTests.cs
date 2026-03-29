using DBToRestAPI.Controllers;

namespace DBToRestAPI.Tests;

/// <summary>
/// Tests for the fire_and_forget property detection logic in embedded HTTP calls.
/// ApiController.IsFireAndForget is an internal static method that parses
/// the "fire_and_forget" property from the {http{...}http} JSON configuration.
/// Also tests the shared CheckBooleanJsonProperty helper that both
/// ShouldSkipHttpCall and IsFireAndForget delegate to.
/// </summary>
public class FireAndForgetPropertyTests
{
    #region Truthy values — should fire and forget

    [Fact]
    public void IsFireAndForget_BooleanTrue_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": true}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_StringTrue_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "true"}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_StringTrueUpperCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "TRUE"}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_StringTrueMixedCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "True"}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_String1_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "1"}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_StringYes_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "yes"}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_StringYesUpperCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "YES"}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_NumberOne_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": 1}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_NumberNonZero_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": 42}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_NegativeNumber_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": -1}""";
        Assert.True(ApiController.IsFireAndForget(json));
    }

    #endregion

    #region Falsy values — should NOT fire and forget

    [Fact]
    public void IsFireAndForget_BooleanFalse_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": false}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_StringFalse_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "false"}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_String0_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "0"}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_StringNo_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "no"}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_NumberZero_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": 0}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_EmptyString_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": ""}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_NullValue_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": null}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_RandomString_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": "maybe"}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    #endregion

    #region Absent property and edge cases

    [Fact]
    public void IsFireAndForget_PropertyAbsent_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "method": "POST"}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_MinimalJson_ReturnsFalse()
    {
        var json = """{"url": "https://example.com"}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_InvalidJson_ReturnsFalse()
    {
        var json = "not valid json at all";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_EmptyObject_ReturnsFalse()
    {
        var json = "{}";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_ObjectValue_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": {"value": true}}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    [Fact]
    public void IsFireAndForget_ArrayValue_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "fire_and_forget": [true]}""";
        Assert.False(ApiController.IsFireAndForget(json));
    }

    #endregion

    #region Combination with skip — both properties present

    [Fact]
    public void IsFireAndForget_WithSkipTrue_BothIndependent()
    {
        var json = """{"url": "https://example.com", "skip": true, "fire_and_forget": true}""";
        Assert.True(ApiController.IsFireAndForget(json));
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void IsFireAndForget_True_SkipFalse_ReturnsCorrectly()
    {
        var json = """{"url": "https://example.com", "skip": false, "fire_and_forget": true}""";
        Assert.True(ApiController.IsFireAndForget(json));
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void IsFireAndForget_False_SkipTrue_ReturnsCorrectly()
    {
        var json = """{"url": "https://example.com", "skip": true, "fire_and_forget": false}""";
        Assert.False(ApiController.IsFireAndForget(json));
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    #endregion

    #region CheckBooleanJsonProperty — shared helper direct tests

    [Fact]
    public void CheckBooleanJsonProperty_CustomPropertyName_Works()
    {
        var json = """{"url": "https://example.com", "my_flag": true}""";
        Assert.True(ApiController.CheckBooleanJsonProperty(json, "my_flag"));
    }

    [Fact]
    public void CheckBooleanJsonProperty_PropertyNotFound_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "other": true}""";
        Assert.False(ApiController.CheckBooleanJsonProperty(json, "my_flag"));
    }

    [Fact]
    public void CheckBooleanJsonProperty_EmptyPropertyName_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "skip": true}""";
        Assert.False(ApiController.CheckBooleanJsonProperty(json, ""));
    }

    [Fact]
    public void CheckBooleanJsonProperty_CaseSensitivePropertyName()
    {
        // JSON property names are case-sensitive
        var json = """{"url": "https://example.com", "Fire_And_Forget": true}""";
        Assert.False(ApiController.CheckBooleanJsonProperty(json, "fire_and_forget"));
    }

    #endregion

    #region ShouldSkipHttpCall regression — ensure refactored version still works

    [Fact]
    public void ShouldSkipHttpCall_Regression_TruthyValues()
    {
        Assert.True(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": true}"""));
        Assert.True(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": "true"}"""));
        Assert.True(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": "1"}"""));
        Assert.True(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": "yes"}"""));
        Assert.True(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": 1}"""));
        Assert.True(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": -5}"""));
    }

    [Fact]
    public void ShouldSkipHttpCall_Regression_FalsyValues()
    {
        Assert.False(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": false}"""));
        Assert.False(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": "false"}"""));
        Assert.False(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": "0"}"""));
        Assert.False(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": "no"}"""));
        Assert.False(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": 0}"""));
        Assert.False(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com", "skip": null}"""));
        Assert.False(ApiController.ShouldSkipHttpCall("""{"url": "https://example.com"}"""));
    }

    #endregion
}
