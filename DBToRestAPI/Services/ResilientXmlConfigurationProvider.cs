using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Xml;

namespace DBToRestAPI.Services;

/// <summary>
/// A resilient XML configuration provider that gracefully handles malformed XML during reload.
/// On initial load, parse errors propagate normally (fail fast).
/// On subsequent reloads (triggered by file changes), parse errors are caught and the
/// last valid configuration is preserved — preventing a typo in one XML file from
/// taking down the entire application's configuration.
/// </summary>
public class ResilientXmlConfigurationProvider : XmlConfigurationProvider
{
    private IDictionary<string, string?>? _lastGoodData;

    public ResilientXmlConfigurationProvider(ResilientXmlConfigurationSource source)
        : base(source) { }

    public override void Load(Stream stream)
    {
        try
        {
            base.Load(stream);
            // Parse succeeded — snapshot the good data
            _lastGoodData = new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (_lastGoodData is not null)
        {
            // Reload failed but we have a previous good state — restore it
            Data = new Dictionary<string, string?>(_lastGoodData, StringComparer.OrdinalIgnoreCase);

            Console.Error.WriteLine(
                $"warn: ResilientXmlConfigurationProvider[0]");
            Console.Error.WriteLine(
                $"      Failed to reload '{Source.Path}': {ex.Message}. " +
                $"Keeping last valid configuration. Fix the XML and save again to retry.");
        }
        // When _lastGoodData is null (initial load), exceptions propagate normally
    }
}

/// <summary>
/// Configuration source for <see cref="ResilientXmlConfigurationProvider"/>.
/// </summary>
public class ResilientXmlConfigurationSource : XmlConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new ResilientXmlConfigurationProvider(this);
    }
}

/// <summary>
/// Extension methods for adding resilient XML configuration files.
/// </summary>
public static class ResilientXmlConfigurationExtensions
{
    /// <summary>
    /// Adds an XML configuration file that gracefully handles malformed XML during reload.
    /// Behaves identically to AddXmlFile except that on reload, parse errors preserve the
    /// last valid configuration instead of clearing all configuration data.
    /// </summary>
    public static IConfigurationBuilder AddResilientXmlFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional = false,
        bool reloadOnChange = false)
    {
        return builder.Add<ResilientXmlConfigurationSource>(s =>
        {
            s.Path = path;
            s.Optional = optional;
            s.ReloadOnChange = reloadOnChange;
        });
    }
}
