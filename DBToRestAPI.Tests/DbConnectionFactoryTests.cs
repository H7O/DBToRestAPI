using DBToRestAPI.Services;

namespace DBToRestAPI.Tests;

public class DbConnectionFactoryTests
{
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
