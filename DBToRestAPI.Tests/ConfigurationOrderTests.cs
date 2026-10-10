using DBToRestAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DBToRestAPI.Tests;

/// <summary>
/// The engine's configuration sources, each overriding the ones before it: appsettings.json from the
/// executable's folder, appsettings.json and appsettings.{Environment}.json from the working directory
/// (the content root), settings.xml, the additional files, environment variables, command-line arguments.
/// </summary>
public class ConfigurationOrderTests : IDisposable
{
    // Not Production: its file configures Kestrel endpoints on every interface.
    private const string EnvironmentName = "OrderTest";

    // The content root: the working directory's appsettings.json and the environment's file.
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "dbtorest-order-" + Guid.NewGuid().ToString("N"));

    // The environment's file beside the executable, which must not be read: in Production it would
    // bind Kestrel to every interface whatever the working directory.
    private readonly string _besideTheExecutable = Path.Combine(AppContext.BaseDirectory, $"appsettings.{EnvironmentName}.json");

    public ConfigurationOrderTests()
    {
        File.WriteAllText(_besideTheExecutable, """{ "order_test_beside_the_executable": "read" }""");
        Directory.CreateDirectory(_contentRoot);
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), """
            {
              "AllowedHosts": "from-the-working-directory"
            }
            """);
        File.WriteAllText(Path.Combine(_contentRoot, $"appsettings.{EnvironmentName}.json"), """
            {
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "debug_mode_header_value": "from-the-environment-file",
              "order_test_key": "from-the-environment-file"
            }
            """);
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();
        Directory.Delete(_contentRoot, recursive: true);
        File.Delete(_besideTheExecutable);
    }

    private IConfiguration Configuration(params (string Key, string Value)[] commandLine)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(EnvironmentName);
            builder.UseContentRoot(_contentRoot);
            // The test host passes these to the engine as command-line arguments.
            foreach (var (key, value) in commandLine)
                builder.UseSetting(key, value);
        });
        _factories.Add(factory);
        return factory.Services.GetRequiredService<IConfiguration>();
    }

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    [Fact]
    public void EachSource_OverridesTheOnesBeforeIt()
    {
        var config = Configuration();

        Assert.Equal("Warning", config["Logging:LogLevel:Default"]);                       // the environment's file over appsettings.json
        Assert.Equal("Warning", config["Logging:LogLevel:System.Net.Http.HttpClient"]);    // appsettings.json, from the executable's folder
        Assert.Equal("from-the-working-directory", config["AllowedHosts"]);                // the working directory's over the executable's
        Assert.Equal("54321", config["debug_mode_header_value"]);                          // settings.xml over the environment's file
        Assert.Equal("from-the-environment-file", config["order_test_key"]);
        Assert.Null(config["order_test_beside_the_executable"]);                         // only the working directory's
        Assert.Equal("5", config["file_management:max_number_of_files"]);                 // an additional file's
    }

    [Fact]
    public void CommandLine_OverridesEveryFile()
    {
        var config = Configuration(("debug_mode_header_value", "from-the-command-line"), ("order_test_key", "from-the-command-line"),
            ("file_management:max_number_of_files", "7"));

        Assert.Equal("from-the-command-line", config["debug_mode_header_value"]);          // over settings.xml
        Assert.Equal("7", config["file_management:max_number_of_files"]);                 // over an additional file
        Assert.Equal("from-the-command-line", config["order_test_key"]);                   // over the environment's file
    }

    [Fact]
    public void EnvironmentVariables_OverrideEveryFile_AndTheCommandLineOverridesThem()
    {
        // A key no other test reads, and a settings.xml value whose small change can't affect another test.
        const string Payload = "max_payload_size_in_bytes";
        var settingsXml = Configuration()[Payload];
        Environment.SetEnvironmentVariable(Payload, "367001601");
        Environment.SetEnvironmentVariable("file_management__max_number_of_files", "6");
        Environment.SetEnvironmentVariable("order_test_key", "from-an-environment-variable");
        try
        {
            Assert.NotEqual("367001601", settingsXml);
            var config = Configuration(("order_test_key", "from-the-command-line"));

            Assert.Equal("367001601", config[Payload]);                                    // over settings.xml
            Assert.Equal("6", config["file_management:max_number_of_files"]);               // over an additional file
            Assert.Equal("from-the-command-line", config["order_test_key"]);                 // over the environment variable
            Assert.Equal("from-an-environment-variable", Configuration()["order_test_key"]); // over the environment's file
        }
        finally
        {
            Environment.SetEnvironmentVariable(Payload, null);
            Environment.SetEnvironmentVariable("file_management__max_number_of_files", null);
            Environment.SetEnvironmentVariable("order_test_key", null);
        }
    }

    [Fact]
    public void CommandLine_OverridesAKeyInAnEncryptedSection_ForTheEngine()
    {
        // The engine reads its settings through the encryption service, which decrypts the shipped
        // encrypted sections (secret_info is one) from their XML files.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("secret_info", "from-the-command-line"));

        Assert.Equal("from-the-command-line", factory.Services.GetRequiredService<IEncryptedConfiguration>()["secret_info"]);
    }

    [Theory]
    [InlineData(new[] { "additional_configurations:path=a.xml" }, new[] { "a.xml" })] // one <path>
    [InlineData(new[] { "additional_configurations:path:0=a.xml", "additional_configurations:path:1=b.json" }, new[] { "a.xml", "b.json" })]
    [InlineData(new string[0], new string[0])]
    public void AdditionalConfigurationPaths_OneOrSeveral(string[] keys, string[] expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(keys.Select(k => k.Split('=')).ToDictionary(kv => kv[0], kv => (string?)kv[1]))
            .Build();

        Assert.Equal(expected, config.AdditionalConfigurationPaths());
    }

    [Fact]
    public void AdditionalConfigurations_OnePath_IsLoaded()
    {
        // As the XML reader gives a single <path> element: a value, not a numbered child.
        File.WriteAllText(Path.Combine(_contentRoot, "settings.xml"), """
            <settings>
              <additional_configurations>
                <path>more.xml</path>
              </additional_configurations>
            </settings>
            """);
        File.WriteAllText(Path.Combine(_contentRoot, "more.xml"), "<settings><only_in_more>read</only_in_more></settings>");
        var settings = new ConfigurationBuilder().SetBasePath(_contentRoot).AddXmlFile("settings.xml").Build();

        var config = new ConfigurationBuilder().SetBasePath(_contentRoot).AddDynamicConfigurationFiles(settings).Build();

        Assert.Equal("read", config["only_in_more"]);
    }

    [Fact]
    public void ShippedSettings_SetTheGatewayKeysTheEngineReads()
    {
        var shipped = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddXmlFile("config/settings.xml").Build();

        Assert.Equal("Host", shipped["excluded_headers"]);
        Assert.Equal("false", shipped["ignore_target_route_certificate_errors"]);
        Assert.Null(shipped["headers_to_exclude_from_routing"]);
        Assert.Null(shipped["ignore_certificate_errors_when_routing"]);
    }

    [Fact]
    public void ShippedSettings_SetNoGlobalCommandTimeout()
    {
        // A global value would override the timeout in every connection string.
        var shipped = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddXmlFile("config/settings.xml").Build();

        Assert.Null(shipped["db_command_timeout"]);
    }

    [Fact]
    public void ShippedFiles_LogAtInformation_AndAtDebugInDevelopment()
    {
        // Each file on its own: either one could otherwise hide the other's level.
        foreach (var file in new[] { "appsettings.json", "appsettings.Production.json" })
        {
            var shipped = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile(file).Build();
            Assert.Equal("Information", shipped["Logging:LogLevel:Default"]);
        }

        // The test host's content root is the engine's project folder, which holds the Development file.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        Assert.Equal("Debug", factory.Services.GetRequiredService<IConfiguration>()["Logging:LogLevel:Default"]);
    }
}
