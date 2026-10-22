using System.Net;
using System.Text;

namespace DBToRestAPI.Tests;

/// <summary>
/// What an <c>{http{...}http}</c> call sends: its body and content type, and a value the request
/// doesn't have. Answered in process by <see cref="FakeHttpServer"/>.
/// </summary>
public class HttpBodyPipelineTests(HttpBodyPipelineTests.BodyEngine engine) : IClassFixture<HttpBodyPipelineTests.BodyEngine>
{
    public sealed class BodyEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        // A form written as a string body, with its type in headers, as the docs once showed.
        ["queries:http_form_header:route"] = "http_form_header",
        ["queries:http_form_header:verb"] = "POST",
        ["queries:http_form_header:query"] =
            """SELECT {http{ {"url": "https://body.test/form", "method": "POST", "headers": {"Content-Type": "application/x-www-form-urlencoded"}, "body": "amount={{amount}}&currency=usd"} }http} AS resp;""",

        ["queries:http_json_header:route"] = "http_json_header",
        ["queries:http_json_header:verb"] = "POST",
        ["queries:http_json_header:query"] =
            """SELECT {http{ {"url": "https://body.test/json", "method": "POST", "headers": {"Content-Type": "application/json"}, "body": {"name": "{{name}}"}} }http} AS resp;""",

        ["queries:http_missing:route"] = "http_missing",
        ["queries:http_missing:verb"] = "POST",
        ["queries:http_missing:query"] =
            """SELECT {http{ {"url": "https://body.test/missing", "method": "POST", "body": {"doc": {{doc}}, "name": "{{name}}"}} }http} AS resp;""",

        // An earlier query's NULL column, outside and inside a string.
        ["queries:http_null_column:route"] = "http_null_column",
        ["queries:http_null_column:verb"] = "POST",
        ["queries:http_null_column:query:0"] = "SELECT NULL AS doc, NULL AS name;",
        ["queries:http_null_column:query:1"] =
            """SELECT {http{ {"url": "https://body.test/null_column", "method": "POST", "body": {"doc": {pq{doc}}, "name": "{pq{name}}"}} }http} AS resp;""",
    });

    private async Task<FakeHttpServer.Request> CallAsync(string route, string body, string path)
    {
        engine.Http.Map("https://body.test" + path, _ => FakeHttpServer.Json(new { ok = true }));
        var before = engine.Http.Requests.Count(r => r.Uri.AbsolutePath == path);

        using var response = await engine.Client.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = engine.Http.Requests.Where(r => r.Uri.AbsolutePath == path).ToList();
        Assert.Equal(before + 1, sent.Count);
        return sent[^1];
    }

    [Fact]
    public async Task AStringBody_IsSentAsWritten_WithTheContentTypeHeadersGives()
    {
        var sent = await CallAsync("http_form_header", """{"amount": "10"}""", "/form");

        Assert.Equal("amount=10&currency=usd", sent.Body);
        Assert.Equal("application/x-www-form-urlencoded", sent.Headers["Content-Type"]);
    }

    [Fact]
    public async Task AContentTypeHeader_ReplacesTheDefault_InsteadOfAddingASecondType()
    {
        var sent = await CallAsync("http_json_header", """{"name": "Ann"}""", "/json");

        Assert.Equal("application/json", sent.Headers["Content-Type"]);
        Assert.Equal("""{"name": "Ann"}""", sent.Body);
    }

    [Fact]
    public async Task AMissingValueOutsideAString_IsNull_AndInsideAString_IsEmpty()
    {
        var sent = await CallAsync("http_missing", "{}", "/missing");

        Assert.Equal("""{"doc": null, "name": ""}""", sent.Body);
    }

    [Fact]
    public async Task AnEarlierQuerysNullColumn_IsNullOutsideAString_AndEmptyInsideOne()
    {
        var sent = await CallAsync("http_null_column", "{}", "/null_column");

        Assert.Equal("""{"doc": null, "name": ""}""", sent.Body);
    }

    [Fact]
    public async Task AValueOutsideAString_IsStillInsertedAsJson()
    {
        var sent = await CallAsync("http_missing", """{"doc": {"a": 1}, "name": "Ann"}""", "/missing");

        Assert.Equal("""{"doc": {"a":1}, "name": "Ann"}""", sent.Body);
    }
}
