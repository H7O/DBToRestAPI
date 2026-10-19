using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// The services the engine builds from its configuration (endpoints, gateway routes, API keys)
/// follow a saved file on its first reload. They used to rebuild before the engine's merged copy
/// of the configuration did, on every other save, so a new endpoint could answer 404 until the
/// file was saved again. Each step below saves the file once and reloads it once.
/// </summary>
public class ConfigurationReloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbtorest-reload-" + Guid.NewGuid().ToString("N"));
    private readonly string _xml;

    public ConfigurationReloadTests()
    {
        Directory.CreateDirectory(_folder);
        _xml = Path.Combine(_folder, "sql.xml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // With a key folder, settings encryption is on everywhere. Without one, it is on (DPAPI) on
    // Windows and off on Linux and macOS. The engine must follow the files in every case.
    private (IConfigurationRoot Root, SettingsEncryptionService Engine) Load(bool keyFolder, string settings)
    {
        Save(settings);
        var builder = new ConfigurationBuilder();
        if (keyFolder)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["settings_encryption:data_protection_key_path"] = Path.Combine(_folder, "keys"),
            });
        }
        var root = builder.AddXmlFile(_xml, optional: false, reloadOnChange: false).Build();
        return (root, new SettingsEncryptionService(root, NullLogger<SettingsEncryptionService>.Instance));
    }

    private void Save(string settings) => File.WriteAllText(_xml, $"<settings>{settings}</settings>");

    // What the file watcher does after a save: the file's provider reads it again and signals the
    // change once. IConfigurationRoot.Reload() would signal it twice, and the second signal would
    // hide a service that rebuilt too early on the first.
    private static void Reload(IConfigurationRoot root) =>
        root.Providers.OfType<FileConfigurationProvider>().Single().Load();

    private static string Queries(params (string Name, string Sql)[] endpoints) =>
        "<queries>"
        + string.Concat(endpoints.Select(e => $"<{e.Name}><route>{e.Name}</route><verb>GET</verb><query>{e.Sql}</query></{e.Name}>"))
        + "</queries>";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Endpoint_Added_Changed_AndRemoved_EachTakeOneReload(bool keyFolder)
    {
        var (root, engine) = Load(keyFolder, Queries(("a", "SELECT 1")));
        var resolver = new QueryRouteResolver(engine);
        Assert.NotNull(resolver.ResolveRoute("a", "GET"));
        Assert.Null(resolver.ResolveRoute("b", "GET"));

        Save(Queries(("a", "SELECT 1"), ("b", "SELECT 2")));
        Reload(root);
        Assert.Equal("SELECT 2", resolver.ResolveRoute("b", "GET")?["query"]);

        // The resolved section is the one the resolver kept, so it shows which reload it came from.
        Save(Queries(("a", "SELECT 1"), ("b", "SELECT 3")));
        Reload(root);
        Assert.Equal("SELECT 3", resolver.ResolveRoute("b", "GET")?["query"]);

        Save(Queries(("a", "SELECT 1")));
        Reload(root);
        Assert.Null(resolver.ResolveRoute("b", "GET"));
        Assert.NotNull(resolver.ResolveRoute("a", "GET"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LastEndpointAndLastGatewayRoute_Removed_NoLongerAnswer(bool keyFolder)
    {
        var (root, engine) = Load(keyFolder,
            Queries(("a", "SELECT 1")) + "<routes><one><url>http://localhost/one</url></one></routes>");
        var resolver = new QueryRouteResolver(engine);
        var gateway = new RouteConfigResolver(engine);
        Assert.NotNull(resolver.ResolveRoute("a", "GET"));
        Assert.NotNull(gateway.ResolveRoute("one"));

        Save("");
        Reload(root);

        Assert.Null(resolver.ResolveRoute("a", "GET"));
        Assert.Null(gateway.ResolveRoute("one"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GatewayRouteAndApiKeyCollection_Added_TakeOneReload(bool keyFolder)
    {
        var (root, engine) = Load(keyFolder,
            "<routes><one><url>http://localhost/one</url></one></routes>"
            + "<api_keys_collections><first><key>k1</key></first></api_keys_collections>");
        var gateway = new RouteConfigResolver(engine);
        var keys = new ApiKeysService(engine, NullLogger<ApiKeysService>.Instance);
        Assert.Null(gateway.ResolveRoute("two"));
        Assert.False(keys.IsValidApiKey("second", "k2"));

        Save("<routes><one><url>http://localhost/one</url></one><two><url>http://localhost/two</url></two></routes>"
            + "<api_keys_collections><first><key>k1</key></first><second><key>k2</key></second></api_keys_collections>");
        Reload(root);

        Assert.NotNull(gateway.ResolveRoute("two"));
        Assert.True(keys.IsValidApiKey("second", "k2"));
    }

    [Fact]
    public void ASection_HandsOutTheSectionsALookupByPathGives()
    {
        var (_, engine) = Load(keyFolder: true, Queries(("a", "SELECT 1")));
        var queries = engine.GetSection("queries");

        Assert.Same(engine.GetSection("queries:a"), queries.GetSection("a"));
        Assert.Same(engine.GetSection("queries:a"), queries.GetSection("A"));
        Assert.Same(engine.GetSection("queries:a:query"), queries.GetSection("a:query"));
        Assert.Equal("SELECT 1", queries.GetSection("a").GetSection("query").Value);

        var missing = queries.GetSection("b");
        Assert.Null(missing.Value);
        Assert.Equal("queries:b", missing.Path);
        Assert.Same(missing, queries.GetSection("b"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASectionKeptAcrossAReload_HandsOutTheNewConfiguration(bool keyFolder)
    {
        var (root, engine) = Load(keyFolder, Queries(("a", "SELECT 1")));
        var queries = engine.GetSection("queries");
        Assert.Equal("SELECT 1", queries.GetSection("a").GetSection("query").Value);

        Save(Queries(("a", "SELECT 2"), ("b", "SELECT 3")));
        Reload(root);

        Assert.Equal("SELECT 2", queries.GetSection("a").GetSection("query").Value);
        Assert.Equal("SELECT 3", queries.GetSection("b").GetSection("query").Value);
    }
}
