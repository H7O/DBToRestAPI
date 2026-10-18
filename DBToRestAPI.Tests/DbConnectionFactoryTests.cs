using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

public class DbConnectionFactoryTests
{
    private static (IConfigurationRoot Configuration, DbConnectionFactory Factory) FactoryFor(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return (configuration, new DbConnectionFactory(new TestEncryptedConfiguration(configuration), NullLogger<DbConnectionFactory>.Instance));
    }

    private static string ProviderConnectionType(DbConnectionFactory factory, string name)
    {
        using var connection = factory.Create(name);
        return ((TrackedDbConnection)connection).Inner.GetType().Name;
    }

    [Fact]
    public void AnEditedProviderAttribute_TakesEffectWithoutARestart()
    {
        var (configuration, factory) = FactoryFor(new()
        {
            ["ConnectionStrings:main"] = "Data Source=:memory:",
            ["ConnectionStrings:main:provider"] = "Microsoft.Data.Sqlite",
        });
        Assert.Equal("SqliteConnection", ProviderConnectionType(factory, "main"));

        configuration["ConnectionStrings:main:provider"] = "Microsoft.Data.SqlClient";

        Assert.Equal("SqlConnection", ProviderConnectionType(factory, "main"));
    }

    [Fact]
    public void AnEditedConnectionString_IsInferredAfresh()
    {
        var (configuration, factory) = FactoryFor(new()
        {
            ["ConnectionStrings:main"] = "Host=localhost;Database=shop;Username=app",
        });
        Assert.Equal("NpgsqlConnection", ProviderConnectionType(factory, "main"));

        configuration["ConnectionStrings:main"] = "Server=.;Database=shop;Trusted_Connection=True;";

        Assert.Equal("SqlConnection", ProviderConnectionType(factory, "main"));
    }

    [Fact]
    public void Sqlite_RelativePath_BecomesAbsolute()
    {
        var result = DbConnectionFactory.NormalizeConnectionStringPaths(
            "Microsoft.Data.Sqlite", "Data Source=demo.db;");

        var expected = Path.Combine(AppContext.BaseDirectory, "demo.db");
        Assert.Contains(expected, result);
    }

    [Fact]
    public void Sqlite_AbsolutePath_RemainsUnchanged()
    {
        var absolutePath = Path.Combine(Path.GetTempPath(), "test.db");
        var connStr = $"Data Source={absolutePath};";

        var result = DbConnectionFactory.NormalizeConnectionStringPaths(
            "Microsoft.Data.Sqlite", connStr);

        Assert.Contains(absolutePath, result);
    }

    [Fact]
    public void Sqlite_MemoryDatabase_RemainsUnchanged()
    {
        var result = DbConnectionFactory.NormalizeConnectionStringPaths(
            "Microsoft.Data.Sqlite", "Data Source=:memory:;");

        Assert.Contains(":memory:", result);
    }

    [Fact]
    public void Sqlite_FileUri_RemainsUnchanged()
    {
        var result = DbConnectionFactory.NormalizeConnectionStringPaths(
            "Microsoft.Data.Sqlite", "Data Source=file:test.db?mode=memory;");

        Assert.Contains("file:test.db?mode=memory", result);
    }

    [Fact]
    public void SqlServer_AttachDbFilename_Relative_BecomesAbsolute()
    {
        var connStr = "Server=.;AttachDbFilename=mydb.mdf;Integrated Security=True;TrustServerCertificate=True;";

        var result = DbConnectionFactory.NormalizeConnectionStringPaths(
            "Microsoft.Data.SqlClient", connStr);

        var expected = Path.Combine(AppContext.BaseDirectory, "mydb.mdf");
        Assert.Contains(expected, result);
    }

    [Fact]
    public void SqlServer_Normal_RemainsUnchanged()
    {
        var connStr = @"Data Source=.\SQLEXPRESS;Initial Catalog=mydb;Integrated Security=True;TrustServerCertificate=True;";

        var result = DbConnectionFactory.NormalizeConnectionStringPaths(
            "Microsoft.Data.SqlClient", connStr);

        Assert.Contains("Initial Catalog=mydb", result);
        Assert.DoesNotContain(AppContext.BaseDirectory, result);
    }

    [Fact]
    public void OtherProvider_PassedThrough()
    {
        var connStr = "Host=localhost;Port=5432;Database=mydb;Username=user;Password=pass;";

        var result = DbConnectionFactory.NormalizeConnectionStringPaths(
            "Npgsql", connStr);

        Assert.Equal(connStr, result);
    }
}
