using System.Collections.Concurrent;
using System.Data.Common;
using DBToRestAPI.Settings;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Services;

public class DbConnectionFactory
{
    private readonly IEncryptedConfiguration _configuration;
    private readonly ILogger<DbConnectionFactory> _logger;
    private readonly ConcurrentDictionary<string, string> _providerCache = new();

    // Track active connections for diagnostics
    private static int _activeConnectionCount = 0;

    public DbConnectionFactory(IEncryptedConfiguration configuration, ILogger<DbConnectionFactory> logger)
    {
        _configuration = configuration;
        _logger = logger;
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

        // Wrap connection to track disposal
        var trackedConnection = new TrackedDbConnection(connection, _logger, connectionStringName);
        var count = Interlocked.Increment(ref _activeConnectionCount);
        _logger.LogDebug(
            "{Time}: Connection CREATED for '{ConnectionStringName}'. Active connections: {Count}",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            connectionStringName,
            count);

        return trackedConnection;
    }

    /// <summary>
    /// Gets the current count of active (non-disposed) connections.
    /// Useful for diagnostics.
    /// </summary>
    public static int ActiveConnectionCount => _activeConnectionCount;

    internal static void DecrementConnectionCount() => Interlocked.Decrement(ref _activeConnectionCount);

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

/// <summary>
/// A wrapper around DbConnection that tracks creation and disposal for diagnostics.
/// This helps identify connection leaks by logging when connections are created and disposed.
/// </summary>
internal class TrackedDbConnection : DbConnection
{
    private readonly DbConnection _inner;
    private readonly ILogger _logger;
    private readonly string _connectionStringName;
    private readonly DateTime _createdAt;
    private bool _disposed;

    public TrackedDbConnection(DbConnection inner, ILogger logger, string connectionStringName)
    {
        _inner = inner;
        _logger = logger;
        _connectionStringName = connectionStringName;
        _createdAt = DateTime.Now;
    }

    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override System.Data.ConnectionState State => _inner.State;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    public override void Open() => _inner.Open();

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _inner.OpenAsync(cancellationToken);
    }

    protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand()
    {
        var cmd = _inner.CreateCommand();
        return cmd;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            var lifetime = DateTime.Now - _createdAt;
            DbConnectionFactory.DecrementConnectionCount();
            var count = DbConnectionFactory.ActiveConnectionCount;

            _logger.LogDebug(
                "{Time}: Connection DISPOSED for '{ConnectionStringName}'. " +
                "Lifetime: {Lifetime}ms. Active connections: {Count}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                _connectionStringName,
                lifetime.TotalMilliseconds.ToString("F0"),
                count);

            if (disposing)
            {
                _inner.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            var lifetime = DateTime.Now - _createdAt;
            DbConnectionFactory.DecrementConnectionCount();
            var count = DbConnectionFactory.ActiveConnectionCount;

            _logger.LogDebug(
                "{Time}: Connection DISPOSED (async) for '{ConnectionStringName}'. " +
                "Lifetime: {Lifetime}ms. Active connections: {Count}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                _connectionStringName,
                lifetime.TotalMilliseconds.ToString("F0"),
                count);

            await _inner.DisposeAsync();
        }
    }
}