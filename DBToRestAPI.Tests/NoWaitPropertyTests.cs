using DBToRestAPI.Controllers;

namespace DBToRestAPI.Tests;

/// <summary>
/// Tests for the no_wait property detection logic in embedded HTTP calls.
/// ApiController.IsNoWait is an internal static method that parses
/// the "no_wait" property from the {http{...}http} JSON configuration.
/// Also tests the shared CheckBooleanJsonProperty helper that both
/// ShouldSkipHttpCall and IsNoWait delegate to.
/// </summary>
public class NoWaitPropertyTests
{
    #region Truthy values — should not wait

    [Fact]
    public void IsNoWait_BooleanTrue_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": true}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_StringTrue_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": "true"}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_StringTrueUpperCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": "TRUE"}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_StringTrueMixedCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": "True"}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_String1_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": "1"}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_StringYes_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": "yes"}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_StringYesUpperCase_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": "YES"}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_NumberOne_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": 1}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_NumberNonZero_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": 42}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_NegativeNumber_ReturnsTrue()
    {
        var json = """{"url": "https://example.com", "no_wait": -1}""";
        Assert.True(ApiController.IsNoWait(json));
    }

    #endregion

    #region Falsy values — should wait for response

    [Fact]
    public void IsNoWait_BooleanFalse_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": false}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_StringFalse_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": "false"}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_String0_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": "0"}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_StringNo_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": "no"}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_NumberZero_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": 0}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_EmptyString_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": ""}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_NullValue_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": null}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_RandomString_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": "maybe"}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    #endregion

    #region Absent property and edge cases

    [Fact]
    public void IsNoWait_PropertyAbsent_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "method": "POST"}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_MinimalJson_ReturnsFalse()
    {
        var json = """{"url": "https://example.com"}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_InvalidJson_ReturnsFalse()
    {
        var json = "not valid json at all";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_EmptyObject_ReturnsFalse()
    {
        var json = "{}";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_ObjectValue_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": {"value": true}}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    [Fact]
    public void IsNoWait_ArrayValue_ReturnsFalse()
    {
        var json = """{"url": "https://example.com", "no_wait": [true]}""";
        Assert.False(ApiController.IsNoWait(json));
    }

    #endregion

    #region Combination with skip — both properties present

    [Fact]
    public void IsNoWait_WithSkipTrue_BothIndependent()
    {
        var json = """{"url": "https://example.com", "skip": true, "no_wait": true}""";
        Assert.True(ApiController.IsNoWait(json));
        Assert.True(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void IsNoWait_True_SkipFalse_ReturnsCorrectly()
    {
        var json = """{"url": "https://example.com", "skip": false, "no_wait": true}""";
        Assert.True(ApiController.IsNoWait(json));
        Assert.False(ApiController.ShouldSkipHttpCall(json));
    }

    [Fact]
    public void IsNoWait_False_SkipTrue_ReturnsCorrectly()
    {
        var json = """{"url": "https://example.com", "skip": true, "no_wait": false}""";
        Assert.False(ApiController.IsNoWait(json));
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
        var json = """{"url": "https://example.com", "No_Wait": true}""";
        Assert.False(ApiController.CheckBooleanJsonProperty(json, "no_wait"));
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
