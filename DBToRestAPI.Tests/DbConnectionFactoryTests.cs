using DBToRestAPI.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

    // Opens a SQLite connection from the factory, runs the SQL, then disposes the connection, and
    // tells whether the engine rolled back. SQLite's rollback hook fires on a ROLLBACK, not on the
    // implicit one when a connection closes, so another test clearing the pools can't fake it.
    private static async Task<(bool RolledBack, CapturedLogs Logs)> DisposeAfterAsync(string sql, bool disposeAsync)
    {
        var file = Path.Combine(Path.GetTempPath(), "dbtorest-tx-" + Guid.NewGuid().ToString("N") + ".db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:main"] = $"Data Source={file}",
            ["ConnectionStrings:main:provider"] = "Microsoft.Data.Sqlite",
        }).Build();
        var logs = new CapturedLogs();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var factory = new DbConnectionFactory(new TestEncryptedConfiguration(configuration), loggers.CreateLogger<DbConnectionFactory>());
        try
        {
            var connection = factory.Create("main");
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            var rolledBack = false;
            SQLitePCL.raw.sqlite3_rollback_hook(((SqliteConnection)((TrackedDbConnection)connection).Inner).Handle, _ => rolledBack = true, null);

            if (disposeAsync)
                await connection.DisposeAsync();
            else
                connection.Dispose();
            return (rolledBack, logs);
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={file}"));
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASqliteTransactionAQueryLeftOpen_IsRolledBack_BeforeTheConnectionIsPooled(bool disposeAsync)
    {
        // Microsoft.Data.Sqlite pools a connection as it is, open transaction and write lock included.
        var (rolledBack, logs) = await DisposeAfterAsync("CREATE TABLE t (x INTEGER); BEGIN; INSERT INTO t VALUES (1);", disposeAsync);

        Assert.True(rolledBack);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData("CREATE TABLE t (x INTEGER); BEGIN; INSERT INTO t VALUES (1); COMMIT;")]
    [InlineData("CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1);")]
    public async Task ASqliteConnectionWithNoOpenTransaction_IsPooledAsItIs(string sql)
    {
        var (rolledBack, logs) = await DisposeAfterAsync(sql, disposeAsync: true);

        Assert.False(rolledBack);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }
}
