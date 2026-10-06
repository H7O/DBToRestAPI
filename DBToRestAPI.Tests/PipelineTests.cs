using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Tests;

/// <summary>
/// The whole engine, in process, against a temporary SQLite database: the controller, the
/// middleware and MVC's streaming of results, which unit tests of the helpers can't reach.
///
/// These cover what 1.7.7 changed when it moved to Com.H.Data.Common 10.1.0.10:
/// - an error raised after the rows gets its status, wherever it surfaces (the controller, or
///   Step6 while MVC streams a result), and is never answered with a 200 or a broken body;
/// - a one-row chain result whose only column has no name is not a {{column}} source;
/// - such a column is returned as the bare value.
/// </summary>
public class PipelineTests(PipelineTests.PipelineEngine engine) : IClassFixture<PipelineTests.PipelineEngine>
{
    private HttpClient Client => engine.Client;

    private const string ThreeRows = "SELECT 1 AS a UNION ALL SELECT 2 UNION ALL SELECT 3";
    private const string Raise = "INSERT INTO raise_after VALUES (1);";

    // About 5,000 characters, longer than one 4 KB buffer segment.
    private const string LongValue = "printf('%.5000c', 'x')";

    private static Dictionary<string, string?> Routes() => new()
    {
        ["queries:pl_array:route"] = "pl/array",
        ["queries:pl_array:verb"] = "GET",
        ["queries:pl_array:response_structure"] = "array",
        ["queries:pl_array:query"] = $"{ThreeRows}; {Raise}",

        // One row with a long value: MVC would hand it to the server before the error arrives, so
        // only the controller reading the result to its end gives the clean status.
        ["queries:pl_array1:route"] = "pl/array1",
        ["queries:pl_array1:verb"] = "GET",
        ["queries:pl_array1:response_structure"] = "array",
        ["queries:pl_array1:query"] = $"SELECT 1 AS a, {LongValue} AS pad; {Raise}",

        ["queries:pl_count1:route"] = "pl/count1",
        ["queries:pl_count1:verb"] = "GET",
        ["queries:pl_count1:count_query"] = "SELECT 1 AS n",
        ["queries:pl_count1:query"] = $"SELECT 1 AS a, {LongValue} AS pad; {Raise}",

        ["queries:pl_count_raise:route"] = "pl/count_raise",
        ["queries:pl_count_raise:verb"] = "GET",
        ["queries:pl_count_raise:count_query"] = $"SELECT 3 AS n; {Raise}",
        ["queries:pl_count_raise:query"] = $"{ThreeRows};",

        ["queries:pl_auto:route"] = "pl/auto",
        ["queries:pl_auto:verb"] = "GET",
        ["queries:pl_auto:query"] = $"{ThreeRows}; {Raise}",

        ["queries:pl_single:route"] = "pl/single",
        ["queries:pl_single:verb"] = "GET",
        ["queries:pl_single:response_structure"] = "single",
        ["queries:pl_single:query"] = $"{ThreeRows}; {Raise}",

        ["queries:pl_count:route"] = "pl/count",
        ["queries:pl_count:verb"] = "GET",
        ["queries:pl_count:count_query"] = "SELECT 3 AS n",
        ["queries:pl_count:query"] = $"{ThreeRows}; {Raise}",

        ["queries:pl_unmapped:route"] = "pl/unmapped",
        ["queries:pl_unmapped:verb"] = "GET",
        ["queries:pl_unmapped:response_structure"] = "array",
        ["queries:pl_unmapped:query"] = $"{ThreeRows}; SELECT * FROM no_such_table;",

        ["queries:pl_ok:route"] = "pl/ok",
        ["queries:pl_ok:verb"] = "GET",
        ["queries:pl_ok:response_structure"] = "array",
        ["queries:pl_ok:query"] = $"{ThreeRows};",

        ["queries:pl_window:route"] = "pl/window/{{n}}",
        ["queries:pl_window:verb"] = "GET",
        ["queries:pl_window:response_structure"] = "array",
        ["queries:pl_window:query"] =
            "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < CAST({{n}} AS INTEGER)) "
            + $"SELECT i AS id, 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' AS pad FROM r; {Raise}",

        ["queries:pl_chain:route"] = "pl/chain",
        ["queries:pl_chain:verb"] = "GET",
        ["queries:pl_chain:query:0"] = "SELECT '{\"id\": 99}' AS \"\";",
        ["queries:pl_chain:query:1"] = "SELECT {{id}} AS id_used, {pq{json}} AS j;",

        ["queries:pl_scalar:route"] = "pl/scalar",
        ["queries:pl_scalar:verb"] = "GET",
        ["queries:pl_scalar:query"] = "SELECT COUNT(*) AS \"\" FROM (SELECT 1 UNION ALL SELECT 2);",

        ["queries:pl_null:route"] = "pl/null",
        ["queries:pl_null:verb"] = "GET",
        ["queries:pl_null:query"] = "SELECT NULL AS \"\";",
    };

    /// <summary>
    /// The engine and its database, shared by the tests of this class and disposed after them.
    /// </summary>
    public sealed class PipelineEngine : IDisposable
    {
        private readonly string _directory;
        private readonly WebApplicationFactory<Program> _factory;
        public HttpClient Client { get; }

        public PipelineEngine()
        {
            _directory = Path.Combine(Path.GetTempPath(), "dbtorest-pipeline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            var database = Path.Combine(_directory, "pipeline.db");

            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // SQLite raises a custom error only from a trigger; the engine maps its [50404] message.
                command.CommandText = """
                    CREATE TABLE raise_after (x INTEGER);
                    CREATE TRIGGER raise_after_trg BEFORE INSERT ON raise_after
                    BEGIN
                      SELECT RAISE(ABORT, '[50404] Category not found');
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            var settings = Routes();
            foreach (var key in settings.Keys.Where(k => k.EndsWith(":route", StringComparison.Ordinal)).ToList())
                settings[key[..^":route".Length] + ":connection_string_name"] = "pipeline";
            settings["ConnectionStrings:pipeline"] = $"Data Source={database}";
            settings["ConnectionStrings:pipeline:provider"] = "Microsoft.Data.Sqlite";

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));
            Client = _factory.CreateClient();
        }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var response = await Client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static void AssertMapped404((HttpStatusCode Status, string Body) response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        var body = JsonDocument.Parse(response.Body).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(404, body.GetProperty("error_number").GetInt32());
        Assert.Contains("Category not found", body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("pl/array")]        // three short rows, streamed by MVC: mapped in Step6
    [InlineData("pl/array1")]       // one long row: read to the end inside the controller
    [InlineData("pl/auto")]
    [InlineData("pl/single")]       // read to the end before answering
    [InlineData("pl/count")]
    [InlineData("pl/count1")]       // one long data row: read to the end inside the controller
    [InlineData("pl/count_raise")]  // the count query itself raises after its row
    public async Task ErrorRaisedAfterTheRows_GetsItsStatus(string path)
    {
        AssertMapped404(await GetAsync(path));
    }

    [Fact]
    public async Task UnmappedErrorAfterTheRows_IsTheGeneric400()
    {
        var (status, body) = await GetAsync("pl/unmapped");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain("no_such_table", body);
        Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task NoError_StillStreamsEveryRow()
    {
        var (status, body) = await GetAsync("pl/ok");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""[{"a":1},{"a":2},{"a":3}]""", body);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(2000)]
    public async Task ErrorAfterRows_IsNeverASuccessOrABrokenBody(int rows)
    {
        // Once part of the result has been written, the status can't change: the engine must cut
        // the connection. Before that, the client gets the clean mapped error. Never a 200, and
        // never the error status with the rows still in front of the error body.
        HttpResponseMessage response;
        string body;
        try
        {
            response = await Client.GetAsync($"pl/window/{rows}");
            body = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return; // the connection was cut (the in-process test server reports it as a cancellation)
        }

        using (response)
        {
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            AssertMapped404((response.StatusCode, body));
        }
    }

    [Fact]
    public async Task Chain_BareValueRow_IsNotAColumnSource()
    {
        // The first query's only column has no name and holds JSON-object text. Read as a column
        // source, its "id" would fill the second query's {{id}} ahead of the request's id=5.
        var (status, body) = await GetAsync("pl/chain?id=5");

        Assert.Equal(HttpStatusCode.OK, status);
        var row = JsonDocument.Parse(body).RootElement;
        Assert.Equal("5", row.GetProperty("id_used").ToString());
        var json = JsonDocument.Parse(row.GetProperty("j").GetString()!).RootElement;
        Assert.Equal(1, json.GetArrayLength());
        Assert.Equal("{\"id\": 99}", json[0].GetString());
    }

    [Fact]
    public async Task UnnamedSingleColumn_IsTheBareValue()
    {
        var (status, body) = await GetAsync("pl/scalar");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2", body);
    }

    [Fact]
    public async Task UnnamedSingleColumn_Null_IsNoRow()
    {
        var (status, body) = await GetAsync("pl/null");

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(string.Empty, body);
    }
}
