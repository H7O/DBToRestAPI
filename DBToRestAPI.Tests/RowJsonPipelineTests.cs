using System.Net;
using System.Text.Json;
using DBToRestAPI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DBToRestAPI.Tests;

/// <summary>
/// The JSON a request gets for each type SQLite returns: one row, a streamed result, a chained
/// query's rows and a cached result. The rows are written by Com.H.Data.Common's converter, and a
/// JSON column's objects by <see cref="ExpandoJsonConverter"/>, byte for byte as the serializer
/// wrote them before either.
/// </summary>
public class RowJsonPipelineTests(RowJsonPipelineTests.Engine engine) : IClassFixture<RowJsonPipelineTests.Engine>
{
    // MVC writes a response with the relaxed encoder, so é stays as it is.
    private const string Rows = """[{"n":1,"t":"a","b":"AA=="},{"n":2,"t":null,"b":null},{"n":3.5,"t":"é","b":"/w=="}]""";
    private const string RowsQuery = "WITH r(n, t, b) AS (VALUES (1, 'a', x'00'), (2, NULL, NULL), (3.5, 'é', x'FF')) SELECT n, t, b FROM r ORDER BY n;";

    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:rj_types:route"] = "rj/types",
        ["queries:rj_types:verb"] = "GET",
        ["queries:rj_types:query"] = """
            SELECT 1 AS i, 9007199254740993 AS big, 2.5 AS r, 'é "q" <b> 😀' AS s, x'0102FF' AS bin, NULL AS n,
                   (1 = 1) AS cmp, '{"a":1.50,"b":[true,null,"x"],"c":{"d":"é"}}' AS {type{json{j}}};
            """,

        ["queries:rj_rows:route"] = "rj/rows",
        ["queries:rj_rows:verb"] = "GET",
        ["queries:rj_rows:response_structure"] = "array",
        ["queries:rj_rows:query"] = RowsQuery,

        ["queries:rj_chain:route"] = "rj/chain",
        ["queries:rj_chain:verb"] = "GET",
        ["queries:rj_chain:query:0"] = RowsQuery,
        ["queries:rj_chain:query:1"] = "SELECT {pq{json}} AS rows_json;",

        ["queries:rj_cached:route"] = "rj/cached",
        ["queries:rj_cached:verb"] = "GET",
        ["queries:rj_cached:response_structure"] = "array",
        ["queries:rj_cached:cache:memory:duration_in_milliseconds"] = "60000",
        ["queries:rj_cached:query"] = RowsQuery,
    });

    private async Task<string> GetAsync(string path)
    {
        using var response = await engine.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public void TheEngine_WritesExpandoObjectsWithTheConverter()
    {
        var options = engine.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        Assert.Single(options.Converters.OfType<ExpandoJsonConverter>());
    }

    [Fact]
    public async Task OneRow_OfEveryType()
    {
        Assert.Equal(
            """{"i":1,"big":9007199254740993,"r":2.5,"s":"é \"q\" <b> \uD83D\uDE00","bin":"AQL/","n":null,"cmp":1,"j":{"a":1.50,"b":[true,null,"x"],"c":{"d":"é"}}}""",
            await GetAsync("rj/types"));
    }

    [Fact]
    public async Task AStreamedResult()
    {
        Assert.Equal(Rows, await GetAsync("rj/rows"));
    }

    [Fact]
    public async Task AChainedQuerysRows()
    {
        // The engine writes a chained query's rows with the serializer's default encoder.
        var body = JsonDocument.Parse(await GetAsync("rj/chain")).RootElement;
        Assert.Equal(
            """[{"n":1,"t":"a","b":"AA=="},{"n":2,"t":null,"b":null},{"n":3.5,"t":"\u00E9","b":"/w=="}]""",
            body.GetProperty("rows_json").GetString());
    }

    [Fact]
    public async Task ACachedResult_FilledAndThenHit()
    {
        Assert.Equal(Rows, await GetAsync("rj/cached"));
        Assert.Equal(Rows, await GetAsync("rj/cached"));
    }
}

/// <summary>
/// docs/topics/05-response-formats.md, "How column values are written": the JSON of each SQL
/// Server column type, as the page's table shows it.
/// </summary>
public class SqlServerColumnTypesTests(SqlServerColumnTypesTests.Engine engine) : IClassFixture<SqlServerColumnTypesTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:column_types:route"] = "column-types",
        ["queries:column_types:verb"] = "GET",
        ["queries:column_types:query"] = """
            SELECT CAST('2026-01-15' AS DATE) AS [date],
                   CAST('2026-01-15T10:20:30.123' AS DATETIME2(3)) AS [datetime2],
                   CAST('2026-01-15T10:20:30.123' AS DATETIME) AS [datetime],
                   CAST('2026-01-15T10:20:30+04:00' AS DATETIMEOFFSET(0)) AS [datetimeoffset],
                   CAST('12:30:05' AS TIME(0)) AS [time],
                   CAST(42 AS INT) AS [int],
                   CAST(9007199254740993 AS BIGINT) AS [bigint],
                   CAST(1.5 AS DECIMAL(10, 2)) AS [decimal],
                   CAST(1.5 AS MONEY) AS [money],
                   CAST(2.5 AS FLOAT) AS [float],
                   CAST(1 AS BIT) AS [bit],
                   CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS UNIQUEIDENTIFIER) AS [uniqueidentifier],
                   CAST(0x0102FF AS VARBINARY(10)) AS [varbinary],
                   CAST(NULL AS INT) AS [null],
                   N'é "q" <b>' AS [nvarchar],
                   CAST(2.5 AS REAL) AS [real],
                   CAST(7 AS SMALLINT) AS [smallint],
                   CAST(255 AS TINYINT) AS [tinyint];
            """,
    }, routesDatabase: (SqlServerExample.Server ?? "", "Microsoft.Data.SqlClient"));

    [SqlServerFact]
    public async Task EachColumnType_IsWrittenAsThePageShows()
    {
        using var response = await engine.Client.GetAsync("column-types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"date":"2026-01-15T00:00:00","datetime2":"2026-01-15T10:20:30.123","datetime":"2026-01-15T10:20:30.123","datetimeoffset":"2026-01-15T10:20:30+04:00","time":"12:30:05","int":42,"bigint":9007199254740993,"decimal":1.50,"money":1.5000,"float":2.5,"bit":true,"uniqueidentifier":"6f9619ff-8b86-d011-b42d-00c04fc964ff","varbinary":"AQL/","null":null,"nvarchar":"é \"q\" <b>","real":2.5,"smallint":7,"tinyint":255}""",
            await response.Content.ReadAsStringAsync());
    }
}
