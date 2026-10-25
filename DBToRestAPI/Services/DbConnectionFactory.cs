using System.Collections.Concurrent;
using System.Data.Common;
using DBToRestAPI.Settings;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Services;

public class DbConnectionFactory
{
    private readonly IEncryptedConfiguration _configuration;
    private readonly ILogger<DbConnectionFactory> _logger;
    // The provider inferred from each connection string. Keyed by the string itself, so an edited
    // connection string is inferred afresh without a restart.
    private readonly ConcurrentDictionary<string, string> _inferredProviders = new();
    // Each connection string as its provider is given it (see NormalizeConnectionStringPaths), worked
    // out once per provider and string rather than parsed again for every connection.
    private readonly ConcurrentDictionary<(string Provider, string ConnectionString), string> _normalizedConnectionStrings = new();

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
        // Register providers LAZILY, by assembly-qualified factory type name. This string overload of
        // RegisterFactory only records the name — the provider assembly is not loaded until the first
        // GetFactory(...) call for that provider. So a deployment that uses only (say) SQLite never
        // loads Oracle/DB2/Npgsql/MySQL/ODBC/OleDb into memory at all. (Passing Factory.Instance
        // instead, as this code previously did, forced every provider assembly to load at startup.)
        //
        // In the "lite" build (dotnet publish -p:DbProviders=lite) the heavy provider packages are not
        // shipped; the registrations below stay harmless because the assembly is only resolved when
        // that provider is actually requested — and Create() then surfaces a clear error.
        DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient");
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite");
        DbProviderFactories.RegisterFactory("Npgsql", "Npgsql.NpgsqlFactory, Npgsql");
        DbProviderFactories.RegisterFactory("MySqlConnector", "MySqlConnector.MySqlConnectorFactory, MySqlConnector");
        DbProviderFactories.RegisterFactory("Oracle.ManagedDataAccess.Core", "Oracle.ManagedDataAccess.Client.OracleClientFactory, Oracle.ManagedDataAccess");
        DbProviderFactories.RegisterFactory("System.Data.Odbc", "System.Data.Odbc.OdbcFactory, System.Data.Odbc");
        DbProviderFactories.RegisterFactory("System.Data.OleDb", "System.Data.OleDb.OleDbFactory, System.Data.OleDb");
        DbProviderFactories.RegisterFactory("Net.IBM.Data.Db2", "IBM.Data.Db2.DB2Factory, IBM.Data.Db2");
    }


    public DbConnection Create(string connectionStringName = "default")
    {
        var connectionString = _configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' not found");

        // The provider attribute, else one inferred from the connection string. Both are read from the
        // current configuration, so a change to either takes effect without a restart.
        var explicitProvider = _configuration[$"ConnectionStrings:{connectionStringName}:provider"];
        var provider = !string.IsNullOrEmpty(explicitProvider)
            ? explicitProvider
            : _inferredProviders.GetOrAdd(connectionString,
                cs => InferProviderFromConnectionString(cs) ?? "Microsoft.Data.SqlClient");

        DbProviderFactory factory;
        try
        {
            factory = DbProviderFactories.GetFactory(provider);
        }
        catch (Exception ex)
        {
            // The provider assembly is resolved lazily on this first use. In a "lite" build only
            // Microsoft.Data.SqlClient and Microsoft.Data.Sqlite are shipped, so any other provider's
            // assembly is absent and fails to resolve here — surface a clear, actionable message.
            throw new InvalidOperationException(
                $"Database provider '{provider}' could not be loaded. If this is a 'lite' build, only "
                + "'Microsoft.Data.SqlClient' and 'Microsoft.Data.Sqlite' are included — use the full "
                + "build for other databases.", ex);
        }

        var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException($"Failed to create connection for provider '{provider}'");

        connection.ConnectionString = _normalizedConnectionStrings.GetOrAdd((provider, connectionString),
            key => NormalizeConnectionStringPaths(key.Provider, key.ConnectionString));

        // SQLite accepts RAISE() only in a trigger: raise_error(50404, 'Not found') works anywhere.
        if (connection is Microsoft.Data.Sqlite.SqliteConnection sqlite)
            DatabaseErrors.AddSqliteRaiseError(sqlite);

        // Wrap connection to track disposal
        var trackedConnection = new TrackedDbConnection(connection, _logger, connectionStringName);
        var count = Interlocked.Increment(ref _activeConnectionCount);
        if (_logger.IsEnabled(LogLevel.Debug))
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

    internal static string NormalizeConnectionStringPaths(string provider, string connectionString)
    {
        if (provider == "Microsoft.Data.Sqlite")
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource;

            if (!string.IsNullOrWhiteSpace(dataSource)
                && dataSource != ":memory:"
                && !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !Path.IsPathRooted(dataSource))
            {
                builder.DataSource = Path.Combine(AppContext.BaseDirectory, dataSource);
            }

            return builder.ToString();
        }

        if (provider == "Microsoft.Data.SqlClient")
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);

            if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename)
                && !Path.IsPathRooted(builder.AttachDBFilename))
            {
                builder.AttachDBFilename = Path.Combine(AppContext.BaseDirectory, builder.AttachDBFilename);
            }

            return builder.ConnectionString;
        }

        return connectionString;
    }

    private static string? InferProviderFromConnectionString(string connectionString)
    {
        var cs = connectionString.ToLowerInvariant();

        // ODBC and OLE DB name their driver. Checked first: the driver's own keywords (a SQL
        // Server driver's TrustServerCertificate, an Oracle one's User Id) match other rules too.
        if (cs.Contains("driver="))
            return "System.Data.Odbc";

        if (cs.Contains("provider=") && (cs.Contains("oledb") || cs.Contains("msoledbsql") ||
            cs.Contains("sqloledb") || cs.Contains("microsoft.ace") || cs.Contains("microsoft.jet") ||
            cs.Contains("msdaora") || cs.Contains("oraoledb")))
            return "System.Data.OleDb";

        // PostgreSQL
        if (cs.Contains("host=") && !cs.Contains("data source="))
            return "Npgsql";

        // MySQL
        if (cs.Contains("sslmode=") || cs.Contains("allowpublickeyretrieval=") ||
            (cs.Contains("server=") && cs.Contains("port=3306")))
            return "MySqlConnector";

        // SQL Server: keywords only it uses. Checked before Oracle, whose rule (User Id with Data
        // Source) a SQL Server login matches too, and before SQLite, whose rule a server named
        // prod.db.example.com would match.
        if (cs.Contains("initial catalog=") || cs.Contains("integrated security=") ||
            cs.Contains("trustservercertificate=") || cs.Contains("trusted_connection=") ||
            cs.Contains("multipleactiveresultsets="))
            return "Microsoft.Data.SqlClient";

        // Oracle
        if (cs.Contains("(description=") || cs.Contains(":1521") ||
            (cs.Contains("user id=") && cs.Contains("data source=")))
            return "Oracle.ManagedDataAccess.Core";

        // SQLite: a database file, or memory, named as the data source itself
        if (SqliteDataSource(connectionString) is { } dataSource
            && (dataSource.Contains(".db") || dataSource.Contains(".sqlite") || dataSource.Contains(":memory:")))
            return "Microsoft.Data.Sqlite";

        // DB2 - detect by typical DB2 connection string patterns
        if ((cs.Contains("database=") && cs.Contains("server=") &&
            (cs.Contains(":50000") || cs.Contains("protocol=tcpip"))) ||
            (cs.Contains("database=") && cs.Contains("uid=") && cs.Contains("pwd=")))
            return "Net.IBM.Data.Db2";

        return null;
    }

    /// <summary>
    /// The value of the keyword SQLite reads its database from (Data Source, DataSource or Filename),
    /// in lower case. A string that doesn't parse is looked at whole, as before.
    /// </summary>
    private static string? SqliteDataSource(string connectionString)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in new[] { "data source", "datasource", "filename" })
            {
                if (builder.TryGetValue(key, out var value) && value is not null)
                    return value.ToString()!.ToLowerInvariant();
            }
            return null;
        }
        catch (ArgumentException)
        {
            return connectionString.ToLowerInvariant();
        }
    }
}

/// <summary>
/// A wrapper around DbConnection that tracks creation and disposal for diagnostics.
/// This helps identify connection leaks by logging when connections are created and disposed.
/// </summary>
internal class TrackedDbConnection : DbConnection
{
    private readonly DbConnection _inner;

    /// <summary>The provider's own connection.</summary>
    internal DbConnection Inner => _inner;
    private readonly ILogger _logger;
    private readonly string _connectionStringName;
    private readonly DateTime _createdAt;
    private bool _disposed;

    public TrackedDbConnection(DbConnection inner, ILogger logger, string connectionStringName)
    {
        _inner = inner;
        _logger = logger;
        _connectionStringName = connectionStringName;
        _createdAt = DateTime.UtcNow;
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

    /// <summary>True once the connection has been opened: a later Closed state means it was lost or closed.</summary>
    internal bool WasOpened { get; private set; }

    public override void Open()
    {
        _inner.Open();
        WasOpened = true;
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _inner.OpenAsync(cancellationToken);
        WasOpened = true;
    }

    protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand()
    {
        var cmd = _inner.CreateCommand();
        return cmd;
    }

    /// <summary>
    /// Has SQLite wait for a lock as long as the query may run: its command timeout, in seconds, else
    /// the connection's default (30). Microsoft.Data.Sqlite leaves SQLite's own busy timeout at 0, so a
    /// statement that finds the database locked fails at once inside the driver, which sleeps 150 ms
    /// before each retry until the command timeout: with many writers, a collision costs a request
    /// 150 ms. SQLite's busy handler retries after 1 ms, then a little later each time, up to every
    /// 100 ms, until the busy timeout, which ends where the command timeout does, so a query still
    /// fails when it did. 0, no limit, waits as long as it takes, as the driver does.
    /// </summary>
    internal void WaitForLocksUpTo(int? commandTimeoutSeconds)
    {
        if (_inner is not Microsoft.Data.Sqlite.SqliteConnection { State: System.Data.ConnectionState.Open } sqlite
            || sqlite.Handle is null)
            return;
        var seconds = commandTimeoutSeconds ?? sqlite.DefaultTimeout;
        var milliseconds = seconds <= 0 ? int.MaxValue : (int)Math.Min(seconds * 1000L, int.MaxValue);
        SQLitePCL.raw.sqlite3_busy_timeout(sqlite.Handle, milliseconds);
    }

    // Microsoft.Data.Sqlite returns a connection to its pool as it is. A transaction that a query's
    // BEGIN opened and an error left open would keep the database's write lock, and the next request
    // given that connection would run inside it. Npgsql rolls back as the connection closes; SqlClient
    // and MySqlConnector reset a pooled connection when it is next used.
    private void RollBackAnOpenSqliteTransaction()
    {
        if (_inner is not Microsoft.Data.Sqlite.SqliteConnection { State: System.Data.ConnectionState.Open } sqlite
            || sqlite.Handle is null
            || SQLitePCL.raw.sqlite3_get_autocommit(sqlite.Handle) != 0)
            return;
        try
        {
            using var rollback = sqlite.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // The transaction stays: keep the connection out of the pool.
            _logger.LogWarning(
                "{Time}: Could not roll back the transaction a query left open on '{ConnectionStringName}': {Message}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), _connectionStringName, LogText.Escape(ex.Message));
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(sqlite);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
                RollBackAnOpenSqliteTransaction();
            _disposed = true;
            var lifetime = DateTime.UtcNow - _createdAt;
            DbConnectionFactory.DecrementConnectionCount();
            var count = DbConnectionFactory.ActiveConnectionCount;

            if (_logger.IsEnabled(LogLevel.Debug))
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
            RollBackAnOpenSqliteTransaction();
            _disposed = true;
            var lifetime = DateTime.UtcNow - _createdAt;
            DbConnectionFactory.DecrementConnectionCount();
            var count = DbConnectionFactory.ActiveConnectionCount;

            if (_logger.IsEnabled(LogLevel.Debug))
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