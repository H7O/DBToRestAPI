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
///
/// And what 1.7.8 changed: `single` is read as the default (one row answers an object, several
/// an array), and file and count-query routes take their first row by reading two, so a query
/// that returns many rows by mistake isn't read to its end. Names keep the caller's spaces, dashes
/// and dots, in every source and in mandatory_parameters.
/// </summary>
public class PipelineTests(PipelineTests.PipelineEngine engine) : IClassFixture<PipelineTests.PipelineEngine>
{
    private HttpClient Client => engine.Client;

    private const string ThreeRows = "SELECT 1 AS a UNION ALL SELECT 2 UNION ALL SELECT 3";
    private const string Raise = "INSERT INTO raise_after VALUES (1);";

    // About 5,000 characters, longer than one 4 KB buffer segment.
    private const string LongValue = "printf('%.5000c', 'x')";

    // A million rows in r(i), for a query that should return one row but doesn't.
    private const string MillionRows =
        "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < 1000000)";

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

        ["queries:pl_single1:route"] = "pl/single1",
        ["queries:pl_single1:verb"] = "GET",
        ["queries:pl_single1:response_structure"] = "single",
        ["queries:pl_single1:query"] = $"SELECT 1 AS a, {LongValue} AS pad; {Raise}",

        ["queries:pl_single_rows:route"] = "pl/single_rows",
        ["queries:pl_single_rows:verb"] = "GET",
        ["queries:pl_single_rows:response_structure"] = "single",
        ["queries:pl_single_rows:query"] = $"{ThreeRows};",

        ["queries:pl_single_one:route"] = "pl/single_one",
        ["queries:pl_single_one:verb"] = "GET",
        ["queries:pl_single_one:response_structure"] = "single",
        ["queries:pl_single_one:query"] = "SELECT 1 AS a;",

        ["queries:pl_auto_one:route"] = "pl/auto_one",
        ["queries:pl_auto_one:verb"] = "GET",
        ["queries:pl_auto_one:response_structure"] = "auto",
        ["queries:pl_auto_one:query"] = "SELECT 1 AS a;",

        // base64_content needs no file store.
        ["queries:pl_file1:route"] = "pl/file1",
        ["queries:pl_file1:verb"] = "GET",
        ["queries:pl_file1:response_structure"] = "file",
        ["queries:pl_file1:query"] = $"SELECT 'a.txt' AS file_name, 'aGk=' AS base64_content; {Raise}",

        ["queries:pl_file_many:route"] = "pl/file_many",
        ["queries:pl_file_many:verb"] = "GET",
        ["queries:pl_file_many:response_structure"] = "file",
        ["queries:pl_file_many:query"] =
            $"{MillionRows} SELECT i || '.txt' AS file_name, 'aGk=' AS base64_content FROM r; {Raise}",

        ["queries:pl_count_many:route"] = "pl/count_many",
        ["queries:pl_count_many:verb"] = "GET",
        ["queries:pl_count_many:count_query"] = $"{MillionRows} SELECT i AS n FROM r; {Raise}",
        ["queries:pl_count_many:query"] = "SELECT 1 AS a;",

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

        // Names exactly as the caller sends them. {{email}} matches nothing (the key is e-mail) and is NULL.
        ["queries:pl_names:route"] = "pl/names",
        ["queries:pl_names:verb"] = "POST",
        ["queries:pl_names:mandatory_parameters"] = "first name, e-mail",
        ["queries:pl_names:query"] =
            "SELECT {{first name}} AS first_name, {{e-mail}} AS email, {{unit price ($)}} AS price, "
            + "{{a.b}} AS dotted, {{email}} AS renamed;",

        // Matched without regard to case. Each name is spelled one way in a query: {{first name}}
        // and {{FIRST NAME}} together would clash on SQL Server.
        ["queries:pl_names_case:route"] = "pl/names_case",
        ["queries:pl_names_case:verb"] = "POST",
        ["queries:pl_names_case:query"] = "SELECT {{FIRST NAME}} AS any_case;",

        // With a `|` in the list, commas are part of the names.
        ["queries:pl_names_pipe:route"] = "pl/names_pipe",
        ["queries:pl_names_pipe:verb"] = "POST",
        ["queries:pl_names_pipe:mandatory_parameters"] = "last, first|e-mail",
        ["queries:pl_names_pipe:query"] = "SELECT {{last, first}} AS full_name;",

        ["queries:pl_names_get:route"] = "pl/names_get",
        ["queries:pl_names_get:verb"] = "GET",
        ["queries:pl_names_get:query"] =
            "SELECT {{first name}} AS first_name, {qs{sort-by}} AS sort_by, {h{X-Tenant-Id}} AS tenant;",
    };

    /// <summary>
    /// The engine and its database, shared by the tests of this class and disposed after them.
    /// </summary>
    public class PipelineEngine : IDisposable
    {
        private readonly string _directory;
        private readonly WebApplicationFactory<Program> _factory;
        public HttpClient Client { get; }

        public PipelineEngine() : this(null) { }

        /// <param name="globalSettings">
        /// Settings outside any route, such as a global response_structure, built from the engine's
        /// temporary folder (for a file store, say).
        /// </param>
        protected PipelineEngine(Func<string, Dictionary<string, string?>>? globalSettings)
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
            foreach (var (key, value) in globalSettings?.Invoke(_directory) ?? [])
                settings[key] = value;

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
    [InlineData("pl/single")]       // read as auto from 1.7.8: three short rows, mapped in Step6
    [InlineData("pl/single1")]      // one long row: read to the end inside the controller
    [InlineData("pl/file1")]        // one row: read to the end before the file is sent
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

    [Theory]
    [InlineData("pl/single_rows", """[{"a":1},{"a":2},{"a":3}]""")] // before 1.7.8: {"a":1}
    [InlineData("pl/single_one", """{"a":1}""")]
    public async Task Single_IsReadAsTheDefault(string path, string expected)
    {
        var (status, body) = await GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task FileRoute_ManyRows_TakesTheFirstWithoutReadingTheRest()
    {
        // Reading the million rows would reach the error raised after them.
        using var response = await Client.GetAsync("pl/file_many");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hi", await response.Content.ReadAsStringAsync());
        Assert.Contains("1.txt", response.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task CountQuery_ManyRows_TakesTheFirstWithoutReadingTheRest()
    {
        var (status, body) = await GetAsync("pl/count_many");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"success":true,"count":1,"data":[{"a":1}]}""", body);
    }

    [Fact]
    public async Task Names_KeepSpacesDashesAndDots_InTheBody()
    {
        using var response = await Client.PostAsync("pl/names", new StringContent(
            """{"first name": "Ann", "e-mail": "ann@example.com", "unit price ($)": 5, "a.b": 7}""",
            System.Text.Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"first_name":"Ann","email":"ann@example.com","price":5,"dotted":7,"renamed":null}""",
            body);
    }

    [Fact]
    public async Task Names_KeepSpacesAndDashes_InAForm()
    {
        using var response = await Client.PostAsync("pl/names", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["first name"] = "Ann",
            ["e-mail"] = "ann@example.com",
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Ann", row.GetProperty("first_name").GetString());
        Assert.Equal("ann@example.com", row.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Names_AreMatchedWithoutRegardToCase()
    {
        using var response = await Client.PostAsync("pl/names_case", new StringContent(
            """{"first name": "Ann"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"any_case":"Ann"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MandatoryParameters_APipeList_KeepsCommasInNames()
    {
        using var ok = await Client.PostAsync("pl/names_pipe", new StringContent(
            """{"last, first": "Doe, Ann", "e-mail": "ann@example.com"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("""{"full_name":"Doe, Ann"}""", await ok.Content.ReadAsStringAsync());

        // The message joins the missing names with the list's own separator.
        using var missing = await Client.PostAsync("pl/names_pipe", new StringContent(
            "{}", System.Text.Encoding.UTF8, "application/json"));
        var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("Missing mandatory parameters: last, first|e-mail", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Names_KeepTheirCharacters_InTheQueryStringAndHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "pl/names_get?first%20name=Ann&sort-by=name");
        request.Headers.Add("X-Tenant-Id", "t1");
        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"first_name":"Ann","sort_by":"name","tenant":"t1"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MandatoryParameters_ANameWithASpace_IsOneName()
    {
        // Before 1.7.8 the list was also split on spaces, so this asked for `first` and `name`.
        using var response = await Client.PostAsync("pl/names", new StringContent(
            """{"e-mail": "ann@example.com"}""", System.Text.Encoding.UTF8, "application/json"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Missing mandatory parameters: first name", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MandatoryParameters_ACommaList_NamesTheMissingOnesWithCommas()
    {
        using var response = await Client.PostAsync("pl/names", new StringContent(
            "{}", System.Text.Encoding.UTF8, "application/json"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Missing mandatory parameters: first name,e-mail", body.GetProperty("message").GetString());
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

/// <summary>
/// A global response_structure under settings is not read from 1.7.8: earlier versions applied it to
/// every route without its own tag. Under a global array, every route still follows its row count.
/// </summary>
public class GlobalResponseStructureTests(GlobalResponseStructureTests.GlobalArrayEngine engine)
    : IClassFixture<GlobalResponseStructureTests.GlobalArrayEngine>
{
    public sealed class GlobalArrayEngine() : PipelineTests.PipelineEngine(_ => new() { ["response_structure"] = "array" });

    [Theory]
    [InlineData("pl/scalar", "2")]                 // no tag: the row-count shape, not the global array
    [InlineData("pl/auto_one", """{"a":1}""")]
    [InlineData("pl/single_one", """{"a":1}""")]
    public async Task GlobalValue_IsNotRead(string path, string expected)
    {
        using var response = await engine.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }
}
