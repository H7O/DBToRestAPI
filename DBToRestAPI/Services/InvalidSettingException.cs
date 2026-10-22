using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace DBToRestAPI.Services;

/// <summary>
/// A setting the engine reads as a number or as true/false holds something else, such as a
/// <c>db_command_timeout</c> of <c>soon</c>. The configuration is wrong, not the request, so
/// <see cref="Middlewares.Step1ServiceTypeChecks"/> answers it with a 500 that names the setting
/// (<see cref="ToResult"/>), whichever step read it. The handlers in between let it through.
/// </summary>
public sealed class InvalidSettingException(string setting, string value, string expected, Exception? innerException = null)
    : Exception($"Invalid {setting} `{value}`: use {expected}", innerException)
{
    public string Setting { get; } = setting;
    public string Value { get; } = value;
    public string Expected { get; } = expected;

    internal ObjectResult ToResult(object? route) => new(new
    {
        success = false,
        message = $"Invalid {Setting} `{Value}` for route `{route}`: use {Expected} "
            + "(Contact your service provider support and provide them with error code `Configuration Error`)"
    })
    {
        StatusCode = 500
    };
}

public static partial class SettingValues
{
    /// <summary>
    /// The value of <paramref name="key"/> read as <typeparamref name="T"/>, as
    /// <c>GetValue&lt;T?&gt;</c> reads it, or null when it is missing or blank.
    /// </summary>
    /// <exception cref="InvalidSettingException">The value isn't a <typeparamref name="T"/>.</exception>
    public static T? GetSetting<T>(this IConfiguration configuration, string key) where T : struct
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return (T?)Converter<T>.Instance.ConvertFromInvariantString(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            throw new InvalidSettingException(SettingName(configuration, key), value,
                typeof(T) == typeof(bool) ? "true or false" : "a whole number", ex);
        }
    }

    /// <summary>
    /// The setting's path as its route or the root has it: <c>cache:memory:duration_in_milliseconds</c>
    /// for a route's, without the <c>queries:name:</c> or <c>routes:name:</c> in front.
    /// </summary>
    private static string SettingName(IConfiguration configuration, string key)
        => configuration is IConfigurationSection section
            ? RoutePrefix().Replace(ConfigurationPath.Combine(section.Path, key), "")
            : key;

    [GeneratedRegex("^(?:queries|routes):[^:]*:", RegexOptions.IgnoreCase)]
    private static partial Regex RoutePrefix();

    private static class Converter<T>
    {
        public static readonly TypeConverter Instance = TypeDescriptor.GetConverter(typeof(T));
    }
}
