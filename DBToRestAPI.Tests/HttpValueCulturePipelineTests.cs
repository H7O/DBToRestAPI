using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace DBToRestAPI.Tests;

/// <summary>
/// What an <c>{http{...}http}</c> call sends for a number, a date or a boolean on a server whose
/// culture writes them otherwise: the same text as on every server, as the engine's own JSON answers
/// write it. Each request runs in the test's culture: the client keeps the test's execution context,
/// so the engine reads the culture set here.
/// </summary>
public class HttpValueCulturePipelineTests(HttpValueCulturePipelineTests.Engine engine) : IClassFixture<HttpValueCulturePipelineTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        // A number and a boolean from the request body, in the query string and inside a JSON string of the body.
        ["queries:culture_request:route"] = "culture_request",
        ["queries:culture_request:verb"] = "POST",
        ["queries:culture_request:query"] =
            """SELECT {http{ {"url": "https://culture.test/request", "method": "POST", "query": {"amount": "{{amount}}", "active": "{{active}}"}, "body": {"amount": "{{amount}}"}} }http} AS resp;""",

        // An earlier query's REAL columns, read with {pq{}}.
        ["queries:culture_chain:route"] = "culture_chain",
        ["queries:culture_chain:verb"] = "POST",
        ["queries:culture_chain:query:0"] = "SELECT 1234.5 AS price, -7.25 AS delta;",
        ["queries:culture_chain:query:1"] =
            """SELECT {http{ {"url": "https://culture.test/chain", "method": "POST", "body": {"price": "{pq{price}}", "delta": "{pq{delta}}"}} }http} AS resp;""",
    });

    /// <summary>de-DE writes a decimal comma, sv-SE also a U+2212 minus sign, ar-SA Arabic separators.</summary>
    public static TheoryData<string> Cultures => ["de-DE", "sv-SE", "ar-SA"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ANumberFromTheRequest_IsSentWithADecimalPoint_AndABooleanAsTrue(string culture)
    {
        var sent = await CallInCultureAsync(engine, culture, "culture_request", """{"amount": 19.99, "active": true}""", "https://culture.test/request");

        var query = QueryHelpers.ParseQuery(sent.Uri.Query);
        Assert.Equal("19.99", query["amount"].ToString());
        Assert.Equal("true", query["active"].ToString());
        Assert.Equal("19.99", JsonDocument.Parse(sent.Body!).RootElement.GetProperty("amount").GetString());
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task AnEarlierQuerysNumbers_AreSentWithADecimalPointAndAPlainMinus(string culture)
    {
        var sent = await CallInCultureAsync(engine, culture, "culture_chain", "{}", "https://culture.test/chain");

        var body = JsonDocument.Parse(sent.Body!).RootElement;
        Assert.Equal("1234.5", body.GetProperty("price").GetString());
        Assert.Equal("-7.25", body.GetProperty("delta").GetString());
    }

    /// <summary>
    /// Calls <paramref name="route"/> with the request's culture set to <paramref name="culture"/>,
    /// and returns what the engine sent to <paramref name="target"/>.
    /// </summary>
    internal static async Task<FakeHttpServer.Request> CallInCultureAsync(
        PipelineTests.PipelineEngine engine, string culture, string route, string body, string target)
    {
        engine.Http.Map(target, _ => FakeHttpServer.Json(new { ok = true }));
        var before = engine.Http.Count(target);

        // Only a client created after this runs the engine in the caller's execution context, and
        // with it the caller's culture.
        engine.Server.PreserveExecutionContext = true;
        using var client = engine.Server.CreateClient();
        var previous = CultureInfo.CurrentCulture;
        HttpResponseMessage response;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            response = await client.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(before + 1 == engine.Http.Count(target), await response.Content.ReadAsStringAsync());
        }
        return engine.Http.Requests.Last(r => r.Uri.GetLeftPart(UriPartial.Path) == target);
    }
}

/// <summary>
/// As <see cref="HttpValueCulturePipelineTests"/>, for the SQL Server column types a date, a decimal,
/// an integer and a bit come back as.
/// </summary>
public class HttpValueCultureSqlServerTests(HttpValueCultureSqlServerTests.Engine engine) : IClassFixture<HttpValueCultureSqlServerTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:culture_columns:route"] = "culture_columns",
        ["queries:culture_columns:verb"] = "POST",
        ["queries:culture_columns:query:0"] = """
            SELECT CAST(1234.50 AS DECIMAL(10, 2)) AS price,
                   CAST(-7 AS INT) AS delta,
                   CAST('2026-10-26T13:45:00' AS DATETIME2(0)) AS [at],
                   CAST('2026-10-26T13:45:00.123' AS DATETIME2(3)) AS at_ms,
                   CAST(1 AS BIT) AS flag,
                   CAST('2026-10-26T13:45:00+02:00' AS DATETIMEOFFSET(0)) AS stamp,
                   CAST('2026-10-26T13:45:00' AS DATETIME2(0)) AS at_outside;
            """,
        ["queries:culture_columns:query:1"] =
            """SELECT {http{ {"url": "https://culture.test/columns", "method": "POST", "body": {"price": "{pq{price}}", "delta": "{pq{delta}}", "at": "{pq{at}}", "at_ms": "{pq{at_ms}}", "flag": "{pq{flag}}", "stamp": "{pq{stamp}}", "at_outside": {pq{at_outside}}}} }http} AS resp;""",
    }, routesDatabase: (SqlServerExample.Server ?? "", "Microsoft.Data.SqlClient"));

    /// <summary>
    /// en-US writes a 12-hour clock, de-DE a decimal comma and day.month.year, sv-SE a U+2212 minus
    /// sign, th-TH the Buddhist year 2569, ar-SA the Hijri calendar.
    /// </summary>
    [SqlServerTheory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public async Task AnEarlierQuerysColumns_AreSentTheSameInEveryCulture(string culture)
    {
        var sent = await HttpValueCulturePipelineTests.CallInCultureAsync(
            engine, culture, "culture_columns", "{}", "https://culture.test/columns");

        var body = JsonDocument.Parse(sent.Body!).RootElement;
        Assert.Equal("1234.50", body.GetProperty("price").GetString());
        Assert.Equal("-7", body.GetProperty("delta").GetString());
        Assert.Equal("2026-10-26T13:45:00", body.GetProperty("at").GetString());
        Assert.Equal("2026-10-26T13:45:00.123", body.GetProperty("at_ms").GetString());
        Assert.Equal("true", body.GetProperty("flag").GetString());
        Assert.Equal("2026-10-26T13:45:00+02:00", body.GetProperty("stamp").GetString());
        Assert.Equal("2026-10-26T13:45:00", body.GetProperty("at_outside").GetString());
    }
}
