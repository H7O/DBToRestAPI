using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DBToRestAPI.Tests;

/// <summary>
/// The whole engine against a temporary SQLite database, for what 1.7.15 added: buffer_rows, which
/// reads a result before writing it so that an error raised after its rows keeps its status; SQLite's
/// raise_error(); 503 for a database the engine can't reach or that drops the connection; 504 for a
/// timeout.
/// </summary>
public class ErrorStatusPipelineTests(ErrorStatusPipelineTests.Engine engine) : IClassFixture<ErrorStatusPipelineTests.Engine>
{
    private const string Raise = "INSERT INTO raise_after VALUES (1);";

    // n rows of about 40 characters each: 2,000 of them are well past the 4 KB the server is handed
    // before the status can no longer change.
    private const string Window =
        "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < CAST({{n}} AS INTEGER)) "
        + "SELECT i AS id, 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' AS pad FROM r";

    /// <summary>A provider whose connection the database closes during the query.</summary>
    internal const string ClosingProvider = "Test.ClosesTheConnection";

    public sealed class Engine() : PipelineTests.PipelineEngine(folder =>
    {
        DbProviderFactories.RegisterFactory(ClosingProvider, ClosingFactory.Instance);
        return new()
        {
            ["queries:es_buffered:route"] = "es/buffered/{{n}}",
            ["queries:es_buffered:verb"] = "GET",
            ["queries:es_buffered:response_structure"] = "array",
            ["queries:es_buffered:buffer_rows"] = "3000",
            ["queries:es_buffered:query"] = $"{Window}; {Raise}",

            ["queries:es_buffered_auto:route"] = "es/buffered_auto/{{n}}",
            ["queries:es_buffered_auto:verb"] = "GET",
            ["queries:es_buffered_auto:buffer_rows"] = "3000",
            ["queries:es_buffered_auto:query"] = $"{Window}; {Raise}",

            ["queries:es_buffered_count:route"] = "es/buffered_count/{{n}}",
            ["queries:es_buffered_count:verb"] = "GET",
            ["queries:es_buffered_count:buffer_rows"] = "3000",
            ["queries:es_buffered_count:count_query"] = "SELECT CAST({{n}} AS INTEGER) AS n",
            ["queries:es_buffered_count:query"] = $"{Window}; {Raise}",

            ["queries:es_buffered_ok:route"] = "es/buffered_ok/{{n}}",
            ["queries:es_buffered_ok:verb"] = "GET",
            ["queries:es_buffered_ok:buffer_rows"] = "3",
            ["queries:es_buffered_ok:query"] = $"{Window};",

            // Its query would answer 404: a 500 shows it never ran.
            ["queries:es_invalid:route"] = "es/invalid",
            ["queries:es_invalid:verb"] = "GET",
            ["queries:es_invalid:buffer_rows"] = "lots",
            ["queries:es_invalid:query"] = Raise,

            // A lookup that answers 404 when it finds nothing: the check follows the SELECT, which
            // returns one row at most.
            ["queries:es_lookup:route"] = "es/lookup/{{id}}",
            ["queries:es_lookup:verb"] = "GET",
            ["queries:es_lookup:query"] =
                "SELECT id FROM (SELECT 1 AS id) WHERE id = CAST({{id}} AS INTEGER); "
                + "SELECT raise_error(50404, 'Record not found') "
                + "WHERE NOT EXISTS (SELECT 1 FROM (SELECT 1 AS id) WHERE id = CAST({{id}} AS INTEGER));",

            // A check inside the write itself, before any row is written.
            ["queries:es_validate:route"] = "es/validate",
            ["queries:es_validate:verb"] = "POST",
            ["queries:es_validate:query"] =
                "INSERT INTO uploads (path) SELECT {{path}} WHERE CASE WHEN {{path}} LIKE '%.txt' THEN 1 "
                + "ELSE raise_error(50400, 'path must end in .txt') END; "
                + "SELECT COUNT(*) AS rows FROM uploads WHERE path = {{path}};",

            // A transaction the error leaves open.
            ["queries:es_tx:route"] = "es/tx",
            ["queries:es_tx:verb"] = "POST",
            ["queries:es_tx:query"] =
                "BEGIN; INSERT INTO uploads (path) VALUES ('tx-left-open'); SELECT raise_error(50409, 'Taken'); COMMIT;",

            ["queries:es_busy:route"] = "es/busy",
            ["queries:es_busy:verb"] = "POST",
            ["queries:es_busy:db_command_timeout"] = "1",
            ["queries:es_busy:query"] = "INSERT INTO uploads (path) VALUES ('busy');",

            ["debug_mode_header_value"] = "es-debug",

            // A chain whose buffer_rows is invalid: its first query would write a row.
            ["queries:es_invalid_chain:route"] = "es/invalid_chain",
            ["queries:es_invalid_chain:verb"] = "POST",
            ["queries:es_invalid_chain:buffer_rows"] = "1e3",
            ["queries:es_invalid_chain:query:0"] = "INSERT INTO uploads (path) VALUES ('chain-ran'); SELECT 1 AS a;",
            ["queries:es_invalid_chain:query:1"] = "SELECT 2 AS b;",

            ["queries:es_missing:route"] = "es/missing",
            ["queries:es_missing:verb"] = "GET",
            ["queries:es_missing:query"] = "SELECT 1 AS a;",
            ["queries:es_missing:query:connection_string_name"] = "missing",
            ["queries:es_missing_count:route"] = "es/missing_count",
            ["queries:es_missing_count:verb"] = "GET",
            ["queries:es_missing_count:count_query"] = "SELECT 1 AS n;",
            ["queries:es_missing_count:query"] = "SELECT 1 AS a;",
            ["queries:es_missing_count:query:connection_string_name"] = "missing",
            ["queries:es_missing_chain:route"] = "es/missing_chain",
            ["queries:es_missing_chain:verb"] = "GET",
            ["queries:es_missing_chain:query:0"] = "SELECT 1 AS a;",
            ["queries:es_missing_chain:query:0:connection_string_name"] = "missing",
            ["queries:es_missing_chain:query:1"] = "SELECT 2 AS b;",
            ["ConnectionStrings:missing"] = $"Data Source={Path.Combine(folder, "missing.db")};Mode=ReadWrite",
            ["ConnectionStrings:missing:provider"] = "Microsoft.Data.Sqlite",

            // The fake provider's reader returns three of the five rows, then fails: with "stream-lost"
            // after closing its connection. The controller reads two, so the failure reaches Step6.
            ["queries:es_lost_stream:route"] = "es/lost_stream/{{kind}}",
            ["queries:es_lost_stream:verb"] = "GET",
            ["queries:es_lost_stream:response_structure"] = "array",
            ["queries:es_lost_stream:query"] =
                "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < 5) SELECT i AS id, {{kind}} AS kind FROM r;",
            ["queries:es_lost_stream:query:connection_string_name"] = "closing",

            // The fake provider's command closes its connection and throws what its text names.
            ["queries:es_lost:route"] = "es/lost/{{kind}}",
            ["queries:es_lost:verb"] = "GET",
            ["queries:es_lost:query"] = "SELECT {{kind}} AS kind;",
            ["queries:es_lost:query:connection_string_name"] = "closing",
            ["ConnectionStrings:closing"] = $"Data Source={Path.Combine(folder, "pipeline.db")}",
            ["ConnectionStrings:closing:provider"] = ClosingProvider,
        };
    });

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path)
    {
        using var response = await engine.Client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static void AssertMapped404((HttpStatusCode Status, JsonElement Body) response, string message = "Category not found")
    {
        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.False(response.Body.GetProperty("success").GetBoolean());
        Assert.Equal(404, response.Body.GetProperty("error_number").GetInt32());
        Assert.Equal(message, response.Body.GetProperty("message").GetString());
    }

    // The engine cut the connection: the in-process server reports it as an exception.
    internal static async Task AssertCutAsync(HttpClient client, string path)
    {
        try
        {
            using var response = await client.GetAsync(path);
            await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return;
        }
        Assert.Fail($"{path} was answered: the connection should have been cut.");
    }

    [Theory]
    [InlineData("es/buffered/2000")]
    [InlineData("es/buffered/3000")]       // exactly buffer_rows
    [InlineData("es/buffered_auto/2000")]
    [InlineData("es/buffered_count/2000")]
    public async Task ARouteWithBufferRows_KeepsTheStatusOfAnErrorAfterTheRows(string path)
    {
        // Without buffer_rows the same result cuts the connection (PipelineTests' pl/window/2000).
        AssertMapped404(await GetAsync(path));
    }

    [Theory]
    [InlineData("es/buffered/3001")]
    [InlineData("es/buffered_count/5000")]
    public async Task AResultLongerThanBufferRows_StillStreams(string path)
    {
        await AssertCutAsync(engine.Client, path);
    }

    [Theory]
    [InlineData(1, """{"id":1,"pad":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""")] // one row: an object
    [InlineData(3, null)]
    [InlineData(5, null)]                                                  // past buffer_rows: streamed
    public async Task ARouteWithBufferRows_AnswersAsWithout(int rows, string? expected)
    {
        using var response = await engine.Client.GetAsync($"es/buffered_ok/{rows}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (expected != null)
            Assert.Equal(expected, body);
        else
            Assert.Equal(rows, JsonDocument.Parse(body).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task AnInvalidBufferRows_IsA500_AndTheQueryDoesNotRun()
    {
        var (status, body) = await GetAsync("es/invalid");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.StartsWith("Invalid buffer_rows `lots` for route `es/invalid`", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RaiseError_AfterALookupThatFindsNothing_IsThe404()
    {
        var (found, row) = await GetAsync("es/lookup/1");
        Assert.Equal(HttpStatusCode.OK, found);
        Assert.Equal(1, row.GetProperty("id").GetInt32());

        AssertMapped404(await GetAsync("es/lookup/2"), "Record not found");
    }

    [Fact]
    public async Task RaiseError_InsideAWrite_StopsIt()
    {
        using var refused = await engine.Client.PostAsync("es/validate",
            new StringContent("""{"path": "a.exe"}""", Encoding.UTF8, "application/json"));
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("path must end in .txt", body.GetProperty("message").GetString());
        Assert.Equal(400, body.GetProperty("error_number").GetInt32());

        using var accepted = await engine.Client.PostAsync("es/validate",
            new StringContent("""{"path": "a.txt"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("""{"rows":1}""", await accepted.Content.ReadAsStringAsync());

        await using var check = new SqliteConnection($"Data Source={Path.Combine(engine.Folder, "pipeline.db")}");
        await check.OpenAsync();
        await using var command = check.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM uploads WHERE path = 'a.exe';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ATransactionAFailedQueryLeftOpen_IsRolledBack_BeforeItsConnectionIsPooled()
    {
        using var response = await engine.Client.PostAsync("es/tx", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // A connection of its own takes the write lock: the pooled one doesn't hold it. Another test
        // class clearing the pools also frees it, so this can pass without the fix when classes run
        // at once: DbConnectionFactoryTests checks the rollback itself.
        await using var other = new SqliteConnection($"Data Source={Path.Combine(engine.Folder, "pipeline.db")};Pooling=False;Default Timeout=5");
        await other.OpenAsync();
        await using var command = other.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE; SELECT COUNT(*) FROM uploads WHERE path = 'tx-left-open';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = "ROLLBACK;";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ADatabaseLockedPastTheCommandTimeout_Is504()
    {
        await using var holder = new SqliteConnection($"Data Source={Path.Combine(engine.Folder, "pipeline.db")};Pooling=False");
        await holder.OpenAsync();
        await using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }
        try
        {
            using var response = await engine.Client.PostAsync("es/busy", new StringContent("{}", Encoding.UTF8, "application/json"));
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal("An error occurred while processing your request.", body.GetProperty("message").GetString());
        }
        finally
        {
            await using var rollback = holder.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task AnInvalidBufferRows_RunsNoQueryOfAChain()
    {
        using var response = await engine.Client.PostAsync("es/invalid_chain", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        await using var check = new SqliteConnection($"Data Source={Path.Combine(engine.Folder, "pipeline.db")}");
        await check.OpenAsync();
        await using var command = check.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM uploads WHERE path = 'chain-ran';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("es/missing")]
    [InlineData("es/missing_count")] // the count query opens the connection
    [InlineData("es/missing_chain")] // a chain's first query
    public async Task ADatabaseTheEngineCanNotOpen_Is503(string path)
    {
        var (status, body) = await GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("An error occurred while processing your request.", body.GetProperty("message").GetString());
        Assert.False(File.Exists(Path.Combine(engine.Folder, "missing.db")));
    }

    [Fact]
    public async Task ADatabaseTheEngineCanNotOpen_InDebugMode_SaysSo()
    {
        // Debug mode keeps its 500 with the details: a failed open is not also a lost connection.
        using var request = new HttpRequestMessage(HttpMethod.Get, "es/missing");
        request.Headers.Add("debug-mode", "es-debug");
        using var response = await engine.Client.SendAsync(request);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.StartsWith("Query 1 of 1 failed: The database could not be reached: SQLite Error 14:",
            body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("lost", HttpStatusCode.ServiceUnavailable)]
    [InlineData("timeout", HttpStatusCode.GatewayTimeout)] // a timeout that broke the connection is still a timeout
    [InlineData("raised", HttpStatusCode.NotFound)]        // and a raised status is still the status
    [InlineData("kept", HttpStatusCode.BadRequest)]        // an error that leaves the connection open is the 400
    public async Task AConnectionTheDatabaseClosed_Is503(string kind, HttpStatusCode expected)
    {
        var (status, _) = await GetAsync($"es/lost/{kind}");

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("stream-lost", HttpStatusCode.ServiceUnavailable)]
    [InlineData("stream-kept", HttpStatusCode.BadRequest)]
    public async Task AConnectionTheDatabaseClosedWhileTheResultStreams_Is503(string kind, HttpStatusCode expected)
    {
        // Nothing has been handed to the server yet, so Step6 writes the error.
        var (status, body) = await GetAsync($"es/lost_stream/{kind}");

        Assert.Equal(expected, status);
        Assert.Equal("An error occurred while processing your request.", body.GetProperty("message").GetString());
    }

    /// <summary>A provider whose commands fail as their text says, closing the connection first.</summary>
    private sealed class ClosingFactory : DbProviderFactory
    {
        public static readonly ClosingFactory Instance = new();
        public override DbConnection CreateConnection() => new ClosingConnection();
    }

    private sealed class ClosingConnection : DbConnection
    {
        private readonly SqliteConnection _inner = new();

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => _inner.ConnectionString; set => _inner.ConnectionString = value; }
        public override string Database => _inner.Database;
        public override string DataSource => _inner.DataSource;
        public override string ServerVersion => _inner.ServerVersion;
        public override ConnectionState State => _inner.State;
        public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
        public override void Close() => _inner.Close();
        public override void Open() => _inner.Open();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => _inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new ClosingCommand(this, _inner.CreateCommand());

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ClosingCommand(ClosingConnection connection, SqliteCommand inner) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get => connection; set { } }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        public override int ExecuteNonQuery() => throw Fail();
        public override object? ExecuteScalar() => throw Fail();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            Kind is "stream-lost" or "stream-kept"
                ? new FailingReader(inner.ExecuteReader(behavior), failAtRow: 4, () =>
                {
                    if (Kind == "stream-lost")
                        connection.Close();
                    return new FakeDbException("Connection reset by peer", null);
                })
                : throw Fail();

        // The kind is the caller's value, bound as the query's only parameter.
        private string? Kind => inner.Parameters.Count > 0 ? inner.Parameters[0].Value as string : null;

        private Exception Fail()
        {
            var kind = Kind;
            if (kind != "kept")
                connection.Close();
            return kind switch
            {
                "timeout" => new FakeDbException("Exception while reading from stream", new TimeoutException()),
                "raised" => new SqliteException("SQLite Error 19: '[50404] Not found'.", 19, 1811),
                _ => new FakeDbException("Connection reset by peer", null),
            };
        }
    }

    private sealed class FakeDbException(string message, Exception? inner) : DbException(message, inner);

    /// <summary>A reader that returns its rows until <paramref name="failAtRow"/>, then throws.</summary>
    private sealed class FailingReader(DbDataReader inner, int failAtRow, Func<Exception> fail) : DbDataReader
    {
        private int _row;

        public override bool Read()
        {
            if (++_row == failAtRow)
                throw fail();
            return inner.Read();
        }

        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>A global buffer_rows applies to every route without its own, and a route's 0 turns it off.</summary>
public class GlobalBufferRowsTests(GlobalBufferRowsTests.Engine engine) : IClassFixture<GlobalBufferRowsTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["buffer_rows"] = "3000",

        ["queries:gb_unbuffered:route"] = "gb/unbuffered/{{n}}",
        ["queries:gb_unbuffered:verb"] = "GET",
        ["queries:gb_unbuffered:response_structure"] = "array",
        ["queries:gb_unbuffered:buffer_rows"] = "0",
        ["queries:gb_unbuffered:query"] =
            "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < CAST({{n}} AS INTEGER)) "
            + "SELECT i AS id, 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' AS pad FROM r; INSERT INTO raise_after VALUES (1);",
    });

    [Fact]
    public async Task TheGlobalValue_KeepsTheStatus()
    {
        // PipelineTests' route, which cuts the connection without buffer_rows.
        using var response = await engine.Client.GetAsync("pl/window/2000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error_number").GetInt32());
    }

    [Fact]
    public async Task ARoutesZero_TurnsItOff()
    {
        await ErrorStatusPipelineTests.AssertCutAsync(engine.Client, "gb/unbuffered/2000");
    }
}
