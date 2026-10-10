using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// A key in a section listed under settings_encryption:sections_to_encrypt is read from its XML file
/// and decrypted, but a later source (an environment variable, a command-line argument) still
/// overrides it, as it does any other key. The engine reads every setting through this service.
/// </summary>
public class EncryptedSettingsPrecedenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbtorest-encryption-" + Guid.NewGuid().ToString("N"));
    private readonly string _xml;

    public EncryptedSettingsPrecedenceTests()
    {
        Directory.CreateDirectory(_folder);
        _xml = Path.Combine(_folder, "secrets.xml");
        File.WriteAllText(_xml, """
            <settings>
              <test_secrets>
                <db>from-the-file</db>
                <other>kept</other>
              </test_secrets>
            </settings>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private SettingsEncryptionService Engine(Dictionary<string, string?>? later = null)
    {
        var root = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["settings_encryption:data_protection_key_path"] = Path.Combine(_folder, "keys"),
                ["settings_encryption:sections_to_encrypt:section"] = "test_secrets",
                ["additional_configurations:path:0"] = _xml,
            })
            .AddXmlFile(_xml, optional: false, reloadOnChange: false)
            .AddInMemoryCollection(later ?? [])     // stands for environment variables or the command line
            .Build();
        return new SettingsEncryptionService(root, NullLogger<SettingsEncryptionService>.Instance);
    }

    [Fact]
    public void ALaterSource_OverridesAnEncryptedKey()
    {
        var engine = Engine(new() { ["test_secrets:db"] = "from-an-environment-variable" });

        Assert.Equal("from-an-environment-variable", engine["test_secrets:db"]);
        Assert.Equal("kept", engine["test_secrets:other"]);
        Assert.Equal("from-an-environment-variable", engine.GetSection("test_secrets")["db"]);
    }

    [Fact]
    public void WithoutAnOverride_TheDecryptedFileValueIsRead_BeforeAndAfterTheFileIsEncrypted()
    {
        // The first engine encrypts the plain values in the file; the second reads the encrypted text.
        Assert.Equal("from-the-file", Engine()["test_secrets:db"]);
        Assert.Contains("encrypted:", File.ReadAllText(_xml));
        Assert.DoesNotContain("from-the-file", File.ReadAllText(_xml));

        Assert.Equal("from-the-file", Engine()["test_secrets:db"]);
    }

    [Fact]
    public void AnOverrideInEncryptedForm_IsDecrypted()
    {
        // A value encrypted with the same keys, as one copied from a deployment that shares them would be.
        Engine();
        var encrypted = System.Xml.Linq.XDocument.Load(_xml).Root!.Element("test_secrets")!.Element("other")!.Value;
        Assert.StartsWith("encrypted:", encrypted);

        var engine = Engine(new() { ["test_secrets:db"] = encrypted });

        Assert.Equal("kept", engine["test_secrets:db"]);
    }

    [Fact]
    public void AValueSetInCode_WinsOverEverySource()
    {
        var engine = Engine(new() { ["test_secrets:db"] = "from-an-environment-variable" });

        engine["test_secrets:db"] = "set-in-code";

        Assert.Equal("set-in-code", engine["test_secrets:db"]);
    }

    [Fact]
    public void AnOverrideThatCantBeDecrypted_GivesWayToTheFileValue()
    {
        // Encrypted text is never a usable value, and this one isn't valid for these keys.
        var engine = Engine(new() { ["test_secrets:other"] = "encrypted:not-valid" });

        Assert.Equal("kept", engine["test_secrets:other"]);
    }
}
