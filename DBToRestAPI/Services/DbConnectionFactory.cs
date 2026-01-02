using System.Collections.Concurrent;
using System.Data.Common;
using DBToRestAPI.Settings;

namespace DBToRestAPI.Services;

public class DbConnectionFactory
{
    private readonly IEncryptedConfiguration _configuration;
    private readonly ConcurrentDictionary<string, string> _providerCache = new();

    public DbConnectionFactory(IEncryptedConfiguration configuration)
    {
        _configuration = configuration;
        RegisterAllProviders();
    }

    private static void RegisterAllProviders()
    {
        DbProviderFactories.RegisterFactory(
            "Microsoft.Data.SqlClient",
            Microsoft.Data.SqlClient.SqlClientFactory.Instance);

        DbProviderFactories.RegisterFactory(
            "Npgsql",
            Npgsql.NpgsqlFactory.Instance);

        DbProviderFactories.RegisterFactory(
            "MySqlConnector",
            MySqlConnector.MySqlConnectorFactory.Instance);

        DbProviderFactories.RegisterFactory(
            "Microsoft.Data.Sqlite",
            Microsoft.Data.Sqlite.SqliteFactory.Instance);

        DbProviderFactories.RegisterFactory(
            "Oracle.ManagedDataAccess.Core",
            Oracle.ManagedDataAccess.Client.OracleClientFactory.Instance);

        // DB2 - Register based on current OS (only one package is included per platform)
        RegisterDb2Provider();

    }

    private static void RegisterDb2Provider()
    {
        try
        {
            // The IBM.Data.Db2 namespace and factory class name is the same across all platform packages
            // Only one package will be present based on the build target OS
            DbProviderFactories.RegisterFactory(
                "Net.IBM.Data.Db2",
                IBM.Data.Db2.DB2Factory.Instance);
        }
        catch 
        {
            // DB2 provider not available on this platform or not included - that's OK
            // Users who don't need DB2 won't have the package
        }
    }


    public DbConnection Create(string connectionStringName = "default")
    {
        var connectionString = _configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' not found");

        var provider = _providerCache.GetOrAdd(connectionStringName, name =>
        {
            // 1. Check explicit provider attribute
            var explicitProvider = _configuration[$"ConnectionStrings:{name}:provider"];
            if (!string.IsNullOrEmpty(explicitProvider))
                return explicitProvider;

            // 2. Auto-detect from connection string
            return InferProviderFromConnectionString(connectionString)
                ?? "Microsoft.Data.SqlClient";
        });

        var factory = DbProviderFactories.GetFactory(provider);
        var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException($"Failed to create connection for provider '{provider}'");

        connection.ConnectionString = connectionString;
        return connection;
    }

    private static string? InferProviderFromConnectionString(string connectionString)
    {
        var cs = connectionString.ToLowerInvariant();

        // PostgreSQL
        if (cs.Contains("host=") && !cs.Contains("data source="))
            return "Npgsql";

        // MySQL
        if (cs.Contains("sslmode=") || cs.Contains("allowpublickeyretrieval=") ||
            (cs.Contains("server=") && cs.Contains("port=3306")))
            return "MySqlConnector";

        // Oracle
        if (cs.Contains("(description=") || cs.Contains(":1521") ||
            (cs.Contains("user id=") && cs.Contains("data source=")))
            return "Oracle.ManagedDataAccess.Core";

        // SQLite
        if (cs.Contains(".db") || cs.Contains(".sqlite") || cs.Contains(":memory:"))
            return "Microsoft.Data.Sqlite";

        // DB2 - detect by typical DB2 connection string patterns
        if ((cs.Contains("database=") && cs.Contains("server=") &&
            (cs.Contains(":50000") || cs.Contains("protocol=tcpip"))) ||
            (cs.Contains("database=") && cs.Contains("uid=") && cs.Contains("pwd=")))
            return "Net.IBM.Data.Db2";

        // SQL Server (most common)
        if (cs.Contains("initial catalog=") || cs.Contains("integrated security=") ||
            cs.Contains("trustservercertificate="))
            return "Microsoft.Data.SqlClient";

        return null;
    }
}