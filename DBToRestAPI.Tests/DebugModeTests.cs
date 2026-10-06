using DBToRestAPI.Settings;
using Microsoft.AspNetCore.Http;

namespace DBToRestAPI.Tests;

/// <summary>
/// When the `debug-mode` header turns on stack traces in error responses.
///
/// Before 1.7.6 an empty debug_mode_header_value matched a `debug-mode` header sent with an
/// empty value, so blanking the setting to switch debug mode off switched it on for anyone.
/// </summary>
public class DebugModeTests
{
    private static IHeaderDictionary Headers(params string[] debugModeValues)
    {
        var headers = new HeaderDictionary();
        if (debugModeValues.Length > 0)
            headers["debug-mode"] = debugModeValues;
        return headers;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankSetting_IsOff_EvenForAnEmptyHeader(string? configured)
    {
        Assert.False(SettingsService.IsDebugMode(configured, Headers("")));
        Assert.False(SettingsService.IsDebugMode(configured, Headers("   ")));
        Assert.False(SettingsService.IsDebugMode(configured, Headers()));
    }

    [Fact]
    public void MatchingHeader_IsOn()
    {
        Assert.True(SettingsService.IsDebugMode("a-long-random-secret", Headers("a-long-random-secret")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a-long-random-secre")]
    [InlineData("a-long-random-secret ")]
    [InlineData("A-LONG-RANDOM-SECRET")]
    public void AnyOtherHeaderValue_IsOff(string sent)
    {
        Assert.False(SettingsService.IsDebugMode("a-long-random-secret", Headers(sent)));
    }

    [Fact]
    public void NoHeader_IsOff()
    {
        Assert.False(SettingsService.IsDebugMode("a-long-random-secret", Headers()));
    }

    [Fact]
    public void SeveralHeaderValues_AreOff()
    {
        Assert.False(SettingsService.IsDebugMode("a-long-random-secret", Headers("a-long-random-secret", "x")));
    }
}
