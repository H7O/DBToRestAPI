using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Tests;

public class AddDynamicConfigurationFilesTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    private string CreateTempFile(string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}{extension}");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private static IConfiguration BuildConfigWithPaths(params string[] paths)
    {
        var dict = new Dictionary<string, string?>();
        for (int i = 0; i < paths.Length; i++)
        {
            dict[$"additional_configurations:path:{i}"] = paths[i];
        }
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private static IConfiguration BuildEmptyConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
    }

    [Fact]
    public void NoSection_ReturnsBuilderUnchanged()
    {
        var config = BuildEmptyConfig();
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        var result = builder.AddDynamicConfigurationFiles(config);

        Assert.Same(builder, result);
        Assert.Equal(initialSourceCount, builder.Sources.Count);
    }

    [Fact]
    public void EmptyPathList_ReturnsBuilderUnchanged()
    {
        // Section exists but has no children
        var dict = new Dictionary<string, string?>
        {
            ["additional_configurations:other_key"] = "value"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        var result = builder.AddDynamicConfigurationFiles(config);

        Assert.Same(builder, result);
        Assert.Equal(initialSourceCount, builder.Sources.Count);
    }

    [Fact]
    public void SingleXmlAbsolutePath_AddsSource()
    {
        var xmlPath = CreateTempFile(".xml", "<settings><key>value</key></settings>");
        var config = BuildConfigWithPaths(xmlPath);
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        builder.AddDynamicConfigurationFiles(config);

        Assert.Equal(initialSourceCount + 1, builder.Sources.Count);
    }

    [Fact]
    public void SingleJsonAbsolutePath_AddsSource()
    {
        var jsonPath = CreateTempFile(".json", """{ "key": "value" }""");
        var config = BuildConfigWithPaths(jsonPath);
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        builder.AddDynamicConfigurationFiles(config);

        Assert.Equal(initialSourceCount + 1, builder.Sources.Count);
    }

    [Fact]
    public void XmlAbsolutePath_ConfigValuesAreLoadable()
    {
        var xmlPath = CreateTempFile(".xml", "<settings><mykey>myvalue</mykey></settings>");
        var config = BuildConfigWithPaths(xmlPath);
        var builder = new ConfigurationBuilder();

        builder.AddDynamicConfigurationFiles(config);
        var builtConfig = builder.Build();

        Assert.Equal("myvalue", builtConfig["mykey"]);
    }

    [Fact]
    public void JsonAbsolutePath_ConfigValuesAreLoadable()
    {
        var jsonPath = CreateTempFile(".json", """{ "settings": { "mykey": "myvalue" } }""");
        var config = BuildConfigWithPaths(jsonPath);
        var builder = new ConfigurationBuilder();

        builder.AddDynamicConfigurationFiles(config);
        var builtConfig = builder.Build();

        Assert.Equal("myvalue", builtConfig["settings:mykey"]);
    }

    [Fact]
    public void MultiplePaths_MixedXmlAndJson_AddsAllSources()
    {
        var xmlPath = CreateTempFile(".xml", "<settings><fromxml>yes</fromxml></settings>");
        var jsonPath = CreateTempFile(".json", """{ "fromjson": "yes" }""");
        var config = BuildConfigWithPaths(xmlPath, jsonPath);
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        builder.AddDynamicConfigurationFiles(config);

        Assert.Equal(initialSourceCount + 2, builder.Sources.Count);
        var builtConfig = builder.Build();
        Assert.Equal("yes", builtConfig["fromxml"]);
        Assert.Equal("yes", builtConfig["fromjson"]);
    }

    [Fact]
    public void UnsupportedExtension_IsSkipped()
    {
        var txtPath = CreateTempFile(".txt", "some content");
        var config = BuildConfigWithPaths(txtPath);
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        builder.AddDynamicConfigurationFiles(config);

        Assert.Equal(initialSourceCount, builder.Sources.Count);
    }

    [Theory]
    [InlineData(".XML")]
    [InlineData(".Xml")]
    public void XmlExtension_CaseInsensitive(string extension)
    {
        var path = CreateTempFile(extension, "<settings><key>value</key></settings>");
        var config = BuildConfigWithPaths(path);
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        builder.AddDynamicConfigurationFiles(config);

        Assert.Equal(initialSourceCount + 1, builder.Sources.Count);
    }

    [Theory]
    [InlineData(".JSON")]
    [InlineData(".Json")]
    public void JsonExtension_CaseInsensitive(string extension)
    {
        var path = CreateTempFile(extension, """{ "key": "value" }""");
        var config = BuildConfigWithPaths(path);
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        builder.AddDynamicConfigurationFiles(config);

        Assert.Equal(initialSourceCount + 1, builder.Sources.Count);
    }

    [Fact]
    public void MixedSupportedAndUnsupported_OnlyAddsSupported()
    {
        var xmlPath = CreateTempFile(".xml", "<settings><key>value</key></settings>");
        var txtPath = CreateTempFile(".txt", "ignored");
        var jsonPath = CreateTempFile(".json", """{ "key": "value" }""");
        var config = BuildConfigWithPaths(xmlPath, txtPath, jsonPath);
        var builder = new ConfigurationBuilder();
        var initialSourceCount = builder.Sources.Count;

        builder.AddDynamicConfigurationFiles(config);

        Assert.Equal(initialSourceCount + 2, builder.Sources.Count);
    }

    [Fact]
    public void ReturnsSameBuilder_ForMethodChaining()
    {
        var config = BuildEmptyConfig();
        var builder = new ConfigurationBuilder();

        var result = builder.AddDynamicConfigurationFiles(config);

        Assert.Same(builder, result);
    }

    [Fact]
    public void RelativePath_ResolvesFromBasePath_NotWorkingDirectory()
    {
        // Create a temp "app root" directory with a config subfolder
        var appRoot = Path.Combine(Path.GetTempPath(), $"test_approot_{Guid.NewGuid()}");
        var configDir = Path.Combine(appRoot, "config");
        Directory.CreateDirectory(configDir);
        _tempDirs.Add(appRoot);

        // Place a config file inside the app root's config/ subfolder
        var xmlFilePath = Path.Combine(configDir, "extra.xml");
        File.WriteAllText(xmlFilePath, "<settings><from_relative>yes</from_relative></settings>");

        var jsonFilePath = Path.Combine(configDir, "extra.json");
        File.WriteAllText(jsonFilePath, """{ "json_relative": "yes" }""");

        // Configure with relative paths (config/extra.xml, config/extra.json)
        var config = BuildConfigWithPaths("config/extra.xml", "config/extra.json");

        // Set base path to the fake app root — NOT the current working directory
        var builder = new ConfigurationBuilder()
            .SetBasePath(appRoot);

        builder.AddDynamicConfigurationFiles(config);
        var builtConfig = builder.Build();

        // Should resolve relative to appRoot, not the test's working directory
        Assert.Equal("yes", builtConfig["from_relative"]);
        Assert.Equal("yes", builtConfig["json_relative"]);
    }

    [Fact]
    public void RelativePath_FailsIfBasePath_NotSet_AndFileNotInWorkingDir()
    {
        // Create a temp directory that is NOT the working directory
        var appRoot = Path.Combine(Path.GetTempPath(), $"test_approot_{Guid.NewGuid()}");
        var configDir = Path.Combine(appRoot, "config");
        Directory.CreateDirectory(configDir);
        _tempDirs.Add(appRoot);

        var xmlFilePath = Path.Combine(configDir, "nonexistent_relative.xml");
        File.WriteAllText(xmlFilePath, "<settings><key>value</key></settings>");

        // Relative path without SetBasePath — will resolve against working dir
        var config = BuildConfigWithPaths("config/nonexistent_relative.xml");
        var builder = new ConfigurationBuilder();

        builder.AddDynamicConfigurationFiles(config);

        // The source is added, but building should fail because the file
        // doesn't exist relative to the working directory
        Assert.ThrowsAny<Exception>(() => builder.Build());
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { }
        }
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
