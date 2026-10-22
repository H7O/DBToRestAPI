using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Tests;

/// <summary>
/// GetSetting reads a value as GetValue does, and names a value it can't read as the route or the
/// root has it.
/// </summary>
public class SettingValuesTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["db_command_timeout"] = " 30 ",
        ["blank"] = " ",
        ["flag"] = "True",
        ["bad_global"] = "soon",
        ["queries:q:cache:memory:duration_in_milliseconds"] = "a minute",
        ["routes:g:ignore_target_route_certificate_errors"] = "yes",
        ["authorize:providers:p:clock_skew_seconds"] = "5m",
    }).Build();

    [Fact]
    public void AValue_IsReadAsGetValueReadsIt()
    {
        Assert.Equal(30, Config.GetSetting<int>("db_command_timeout"));
        Assert.Equal(30L, Config.GetSetting<long>("db_command_timeout"));
        Assert.True(Config.GetSetting<bool>("flag"));
        Assert.Null(Config.GetSetting<int>("blank"));
        Assert.Null(Config.GetSetting<int>("missing"));
    }

    [Theory]
    [InlineData("", "bad_global", "bad_global", "soon", "a whole number")]
    [InlineData("queries:q:cache:memory", "duration_in_milliseconds", "cache:memory:duration_in_milliseconds", "a minute", "a whole number")]
    [InlineData("routes:g", "ignore_target_route_certificate_errors", "ignore_target_route_certificate_errors", "yes", "true or false")]
    [InlineData("authorize:providers:p", "clock_skew_seconds", "authorize:providers:p:clock_skew_seconds", "5m", "a whole number")]
    public void AnInvalidValue_IsNamedAsItsRouteOrTheRootHasIt(string section, string key, string name, string value, string expected)
    {
        IConfiguration from = section == "" ? Config : Config.GetSection(section);

        var ex = expected == "true or false"
            ? Assert.Throws<InvalidSettingException>(() => from.GetSetting<bool>(key))
            : Assert.Throws<InvalidSettingException>(() => from.GetSetting<int>(key));

        Assert.Equal(name, ex.Setting);
        Assert.Equal(value, ex.Value);
        Assert.Equal(expected, ex.Expected);
    }
}
