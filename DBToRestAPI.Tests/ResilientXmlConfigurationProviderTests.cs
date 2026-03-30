using System.Text;
using DBToRestAPI.Services;

namespace DBToRestAPI.Tests;

public class ResilientXmlConfigurationProviderTests
{
    private static ResilientXmlConfigurationProvider CreateProvider()
    {
        var source = new ResilientXmlConfigurationSource
        {
            Path = "test.xml",
            Optional = true,
            ReloadOnChange = false
        };
        return new ResilientXmlConfigurationProvider(source);
    }

    private static Stream ToStream(string xml) =>
        new MemoryStream(Encoding.UTF8.GetBytes(xml));

    private const string ValidXml1 = """
        <?xml version="1.0" encoding="utf-8"?>
        <settings>
          <app_name>MyApp</app_name>
          <version>1.0</version>
        </settings>
        """;

    private const string ValidXml2 = """
        <?xml version="1.0" encoding="utf-8"?>
        <settings>
          <app_name>MyApp Updated</app_name>
          <version>2.0</version>
        </settings>
        """;

    private const string BrokenXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <settings>
          <app_name>Broken
        """;

    [Fact]
    public void InitialLoad_ValidXml_ConfigValuesAccessible()
    {
        var provider = CreateProvider();

        provider.Load(ToStream(ValidXml1));

        Assert.True(provider.TryGet("app_name", out var appName));
        Assert.Equal("MyApp", appName);
        Assert.True(provider.TryGet("version", out var version));
        Assert.Equal("1.0", version);
    }

    [Fact]
    public void Reload_ValidXml_ConfigUpdatesToNewValues()
    {
        var provider = CreateProvider();
        provider.Load(ToStream(ValidXml1));

        provider.Load(ToStream(ValidXml2));

        Assert.True(provider.TryGet("app_name", out var appName));
        Assert.Equal("MyApp Updated", appName);
        Assert.True(provider.TryGet("version", out var version));
        Assert.Equal("2.0", version);
    }

    [Fact]
    public void Reload_BrokenXml_PreservesLastGoodConfig()
    {
        var provider = CreateProvider();
        provider.Load(ToStream(ValidXml1));

        // This should NOT throw — it should silently keep the last good config
        provider.Load(ToStream(BrokenXml));

        Assert.True(provider.TryGet("app_name", out var appName));
        Assert.Equal("MyApp", appName);
        Assert.True(provider.TryGet("version", out var version));
        Assert.Equal("1.0", version);
    }

    [Fact]
    public void Reload_BrokenThenFixed_RecoverWithNewValues()
    {
        var provider = CreateProvider();
        provider.Load(ToStream(ValidXml1));

        // Break it
        provider.Load(ToStream(BrokenXml));

        // Fix it with new values
        provider.Load(ToStream(ValidXml2));

        Assert.True(provider.TryGet("app_name", out var appName));
        Assert.Equal("MyApp Updated", appName);
        Assert.True(provider.TryGet("version", out var version));
        Assert.Equal("2.0", version);
    }

    [Fact]
    public void InitialLoad_BrokenXml_ThrowsException()
    {
        var provider = CreateProvider();

        Assert.ThrowsAny<Exception>(() => provider.Load(ToStream(BrokenXml)));
    }

    [Fact]
    public void Reload_MultipleBrokenReloads_StillPreservesOriginalGoodConfig()
    {
        var provider = CreateProvider();
        provider.Load(ToStream(ValidXml1));

        // Break it multiple times
        provider.Load(ToStream(BrokenXml));
        provider.Load(ToStream(BrokenXml));
        provider.Load(ToStream(BrokenXml));

        // Original values still intact
        Assert.True(provider.TryGet("app_name", out var appName));
        Assert.Equal("MyApp", appName);
    }

    [Fact]
    public void Reload_BrokenXml_DoesNotAddOrRemoveKeys()
    {
        var provider = CreateProvider();
        provider.Load(ToStream(ValidXml1));

        provider.Load(ToStream(BrokenXml));

        // Should still have exactly the same keys
        Assert.True(provider.TryGet("app_name", out _));
        Assert.True(provider.TryGet("version", out _));
    }
}
