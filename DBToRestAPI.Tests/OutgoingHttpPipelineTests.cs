using System.Net;
using System.Text;
using System.Text.Json;
using DBToRestAPI.Services;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// The whole engine making its outgoing calls, answered in process by <see cref="FakeHttpServer"/>:
/// a download query's <c>http</c> source, an API gateway route, and <c>{http{...}}</c> calls in SQL.
/// </summary>
public class OutgoingHttpPipelineTests(OutgoingHttpPipelineTests.OutgoingEngine engine)
    : IClassFixture<OutgoingHttpPipelineTests.OutgoingEngine>
{
    public sealed class OutgoingEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:http_file:route"] = "http_file/{{name}}",
        ["queries:http_file:verb"] = "GET",
        ["queries:http_file:response_structure"] = "file",
        ["queries:http_file:query"] = "SELECT {{name}} AS file_name, 'https://files.test/' || {{name}} AS http;",

        // A signed URL with credentials: none of its secrets may reach the caller or the log.
        ["queries:http_signed:route"] = "http_signed",
        ["queries:http_signed:verb"] = "GET",
        ["queries:http_signed:response_structure"] = "file",
        ["queries:http_signed:query"] =
            "SELECT 'a.txt' AS file_name, 'https://user:SECRET-PASSWORD@files.test/private.txt?sig=SECRET-SIGNATURE' AS http;",

        ["queries:http_invalid:route"] = "http_invalid",
        ["queries:http_invalid:verb"] = "GET",
        ["queries:http_invalid:response_structure"] = "file",
        ["queries:http_invalid:query"] = "SELECT 'a.txt' AS file_name, 'files.test/a.txt?sig=SECRET-SIGNATURE' AS http;",

        ["queries:http_marker:route"] = "http_marker",
        ["queries:http_marker:verb"] = "POST",
        ["queries:http_marker:query"] =
            """SELECT {http{ {"url": "https://echo.test/echo", "method": "POST", "body": {{payload}} } }http} AS resp;""",

        // A key written into the url string, as some APIs require. It must never reach the log.
        ["queries:http_keyed:route"] = "http_keyed/{{path}}",
        ["queries:http_keyed:verb"] = "GET",
        ["queries:http_keyed:query"] =
            """SELECT {http{ {"url": "https://keyed.test/{{path}}?key=SECRET-KEY", "method": "GET", "headers": {"Ocp-Apim-Subscription-Key": "SECRET-HEADER"}} }http} AS resp;""",

        // A header name built from a caller value.
        ["queries:http_header_name:route"] = "http_header_name",
        ["queries:http_header_name:verb"] = "GET",
        ["queries:http_header_name:query"] =
            """SELECT {http{ {"url": "https://keyed.test/ok", "method": "GET", "headers": {"X-{{tenant}}": "v"}} }http} AS resp;""",

        // A call in an earlier query, then a skipped or no_wait call in a later one, which must get NULL.
        ["queries:http_chain_skip:route"] = "http_chain_skip",
        ["queries:http_chain_skip:verb"] = "GET",
        ["queries:http_chain_skip:query:0"] = """SELECT {http{ {"url": "https://later.test/first", "method": "GET"} }http} AS first_response;""",
        ["queries:http_chain_skip:query:1"] = """SELECT {http{ {"url": "https://later.test/second", "method": "GET", "skip": true} }http} AS second;""",

        ["queries:http_chain_no_wait:route"] = "http_chain_no_wait",
        ["queries:http_chain_no_wait:verb"] = "GET",
        ["queries:http_chain_no_wait:query:0"] = """SELECT {http{ {"url": "https://later.test/first", "method": "GET"} }http} AS first_response;""",
        ["queries:http_chain_no_wait:query:1"] = """SELECT {http{ {"url": "https://later.test/background", "method": "GET", "no_wait": true} }http} AS second;""",

        ["queries:http_count_skip:route"] = "http_count_skip",
        ["queries:http_count_skip:verb"] = "GET",
        ["queries:http_count_skip:count_query"] = """SELECT CASE WHEN {http{ {"url": "https://later.test/first", "method": "GET"} }http} IS NULL THEN 0 ELSE 1 END AS n;""",
        ["queries:http_count_skip:query"] = """SELECT {http{ {"url": "https://later.test/second", "method": "GET", "skip": true} }http} AS second;""",

        // One name under two markers of one pattern: both get the value.
        ["queries:http_two_markers:route"] = "http_two_markers",
        ["queries:http_two_markers:verb"] = "POST",
        ["queries:http_two_markers:query"] =
            """SELECT {http{ {"url": "https://two.test/a/{{id}}/b/{j{id}}", "method": "GET"} }http} AS resp;""",

        // An earlier query's document, written whole into the call's body. Markers inside it are its
        // text: they must not pull in a setting or another request value.
        ["vars:secret_key"] = "SECRET-VAR",
        ["queries:http_pq_doc:route"] = "http_pq_doc",
        ["queries:http_pq_doc:verb"] = "POST",
        ["queries:http_pq_doc:query:0"] = "SELECT {{note}} AS doc;",
        ["queries:http_pq_doc:query:1"] = """SELECT {http{ {"url": "https://echo.test/echo", "method": "POST", "body": {pq{doc}} } }http} AS resp;""",

        ["queries:http_pq_doc_pipes:route"] = "http_pq_doc_pipes",
        ["queries:http_pq_doc_pipes:verb"] = "POST",
        ["queries:http_pq_doc_pipes:json_variables_pattern"] = @"(?<open_marker>\|\|)(?<param>.*?)?(?<close_marker>\|\|)",
        ["queries:http_pq_doc_pipes:query:0"] = "SELECT ||note|| AS doc;",
        ["queries:http_pq_doc_pipes:query:1"] = """SELECT {http{ {"url": "https://echo.test/echo", "method": "POST", "body": {pq{doc}} } }http} AS resp;""",

        // Applied headers: one child, children with their own names, and repeated <header> siblings,
        // which the XML reader numbers (header:0, header:1).
        ["routes:gw_applied:route"] = "gw_applied",
        ["routes:gw_applied:url"] = "https://upstream.test/headers",
        ["routes:gw_applied:excluded_headers"] = "host,x-drop",
        ["routes:gw_applied:applied_headers:header:name"] = "X-One",
        ["routes:gw_applied:applied_headers:header:value"] = "1",

        ["routes:gw_applied_named:route"] = "gw_applied_named",
        ["routes:gw_applied_named:url"] = "https://upstream.test/headers",
        ["routes:gw_applied_named:excluded_headers"] = "host",
        ["routes:gw_applied_named:applied_headers:api_key:name"] = "X-Api-Key",
        ["routes:gw_applied_named:applied_headers:api_key:value"] = "k",
        ["routes:gw_applied_named:applied_headers:accept:name"] = "Accept",
        ["routes:gw_applied_named:applied_headers:accept:value"] = "application/json",

        ["routes:gw_applied_repeated:route"] = "gw_applied_repeated",
        ["routes:gw_applied_repeated:url"] = "https://upstream.test/headers",
        ["routes:gw_applied_repeated:excluded_headers"] = "host",
        ["routes:gw_applied_repeated:applied_headers:header:0:name"] = "X-A",
        ["routes:gw_applied_repeated:applied_headers:header:0:value"] = "a",
        ["routes:gw_applied_repeated:applied_headers:header:1:name"] = "X-B",
        ["routes:gw_applied_repeated:applied_headers:header:1:value"] = "b",

        // No list of its own: the global excluded_headers applies.
        ["excluded_headers"] = "host,x-global-drop",
        ["routes:gw_global_excluded:route"] = "gw_global_excluded",
        ["routes:gw_global_excluded:url"] = "https://upstream.test/headers",

        ["routes:gw:route"] = "gw/*",
        ["routes:gw:url"] = "https://upstream.test/",
        ["routes:gw:excluded_headers"] = "host",
        ["routes:gw:cache:memory:duration_in_milliseconds"] = "60000",
        ["routes:gw:cache:memory:exclude_status_codes_from_cache"] = "400",
    });

    private FakeHttpServer Http => engine.Http;

    private static bool HasSecret(string text) => text.Contains("SECRET", StringComparison.Ordinal);

    [Fact]
    public async Task HttpSource_StreamsTheRemoteFile_WithTheRemoteType()
    {
        // The name's extension says text/plain; the remote's type wins.
        Http.Map("https://files.test/report.txt", _ => FakeHttpServer.Text("a,b\n1,2", "text/csv"));

        using var response = await engine.Client.GetAsync("http_file/report.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a,b\n1,2", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("report.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task HttpSource_ALargeFile_ArrivesWhole()
    {
        var bytes = new byte[3 * 1024 * 1024 + 7];
        new Random(7).NextBytes(bytes);
        Http.Map("https://files.test/large.txt", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });

        using var response = await engine.Client.GetAsync("http_file/large.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The remote sent no type: application/octet-stream, not the text/plain of the name's extension.
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task HttpSource_ARemoteError_KeepsItsStatus_AndTheUrlStaysPrivate()
    {
        // A status the engine never answers on its own, so only a pass-through gives it.
        Http.Map("https://files.test/private.txt", _ => FakeHttpServer.Text("no", status: HttpStatusCode.Forbidden));

        using var response = await engine.Client.GetAsync("http_signed");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.StartsWith("Failed to download file for route", JsonDocument.Parse(body).RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("files.test", body);
        Assert.False(HasSecret(body), body);

        Assert.Contains(engine.Logs.Entries, e => e.Message.Contains("`https://files.test/private.txt` answered 403"));
        var leaks = engine.Logs.Entries.Where(e => HasSecret(e.Text)).Select(e => $"{e.Category}: {e.Text}").ToList();
        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));
    }

    [Fact]
    public async Task HttpSource_NotAnAbsoluteUrl_Is404WithoutTheValue()
    {
        using var response = await engine.Client.GetAsync("http_invalid");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("Invalid HTTP URL for route", JsonDocument.Parse(body).RootElement.GetProperty("message").GetString());
        Assert.False(HasSecret(body), body);
        Assert.Contains(engine.Logs.Entries, e => e.Message.Contains($"Invalid HTTP URL `{LogText.NotANetworkUrl}`"));
        Assert.DoesNotContain(engine.Logs.Entries, e => HasSecret(e.Text));
    }

    [Theory]
    [InlineData("https://files.test/a.txt", "https://files.test/a.txt")]
    [InlineData("https://user:pw@files.test:8443/a b.txt?sig=x#part", "https://files.test:8443/a%20b.txt")]
    [InlineData("ftp://files.test/a.txt?sig=x", LogText.NotANetworkUrl)]
    // Not a network URL: a relative or scheme-relative value, or a file path, which .NET reads as a file
    // URI in which `?` is an ordinary character, or text that doesn't parse.
    [InlineData("files.test/a.txt?sig=x", LogText.NotANetworkUrl)]
    [InlineData("//cdn.test/a.pdf?sig=x", LogText.NotANetworkUrl)]
    [InlineData("/files/a.pdf?sig=x", LogText.NotANetworkUrl)]
    [InlineData(@"C:\files\a.pdf?sig=x", LogText.NotANetworkUrl)]
    [InlineData(@"\\server\share\a.pdf?sig=x", LogText.NotANetworkUrl)]
    [InlineData("https://svc:P@ss@api.test/x?sig=x", LogText.NotANetworkUrl)]
    [InlineData("https://svc:ab#cd@api.test/x", LogText.NotANetworkUrl)]
    [InlineData("bad\nline?sig=x", LogText.NotANetworkUrl)]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LogTextUrl_DropsWhatMayBeSecret(string? url, string expected)
    {
        Assert.Equal(expected, LogText.Url(url));
    }

    [Fact]
    public async Task HttpMarker_KeyInTheUrl_NeverReachesTheLog_AndASuccessIsLoggedAtDebug()
    {
        Http.Map("https://keyed.test/ok", _ => FakeHttpServer.Json(new { r = "reached" }));
        Http.Map("https://keyed.test/missing", _ => FakeHttpServer.Text("no", status: HttpStatusCode.NotFound));

        using (var ok = await engine.Client.GetAsync("http_keyed/ok"))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var missing = await engine.Client.GetAsync("http_keyed/missing"))
            Assert.Equal(HttpStatusCode.OK, missing.StatusCode);   // the SQL gets the remote's 404 to inspect

        Assert.Equal("SECRET-KEY", Http.Requests.Last(r => r.Uri.Host == "keyed.test").Uri.Query.Split('=')[1]);
        var leaks = engine.Logs.Entries.Where(e => HasSecret(e.Text)).Select(e => $"{e.Category}: {e.Text}").ToList();
        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));

        // Each call is logged once at its level: a success at Debug, so the shipped Information log
        // stays quiet, and a failure at Warning. The Debug request line names the headers, never their values.
        var executor = engine.Logs.Entries.Where(e => e.Category.StartsWith("DBToRestAPI.Services.HttpExecutor", StringComparison.Ordinal)).ToList();
        Assert.Contains(executor, e => e.Message.Contains("""Headers: ["Ocp-Apim-Subscription-Key"]"""));
        Assert.Contains(executor, e => e.Message.Contains("https://keyed.test/ok -> 200") && e.Level == LogLevel.Debug);
        Assert.Contains(executor, e => e.Message.Contains("https://keyed.test/missing -> 404") && e.Level == LogLevel.Warning);
        Assert.DoesNotContain(executor, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task HttpMarker_AHeaderNameFromTheCaller_CantAddALineToTheLog()
    {
        Http.Map("https://keyed.test/ok", _ => FakeHttpServer.Json(new { r = "reached" }));

        using var response = await engine.Client.GetAsync("http_header_name?tenant=a%0D%0AFAKE-ENTRY");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(engine.Logs.Entries, e => e.Message.Contains("FAKE-ENTRY"));
        Assert.DoesNotContain(engine.Logs.Entries, e => e.Message.Contains("\nFAKE-ENTRY") || e.Message.Contains("\rFAKE-ENTRY"));
    }

    [Fact]
    public async Task Gateway_ForwardsAGet_AndAnswersTheNextFromTheCache()
    {
        Http.Map("https://upstream.test/hello", _ => FakeHttpServer.Text("hello"));
        var calls = Http.Count("https://upstream.test/hello");

        for (var i = 0; i < 2; i++)
        {
            using var response = await engine.Client.GetAsync("gw/hello");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("hello", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(calls + 1, Http.Count("https://upstream.test/hello"));
    }

    [Fact]
    public async Task Gateway_AStatusExcludedFromTheCache_IsForwardedEveryTime()
    {
        Http.Map("https://upstream.test/bad", _ => FakeHttpServer.Json(new { error = "bad input" }, HttpStatusCode.BadRequest));
        var calls = Http.Count("https://upstream.test/bad");

        for (var i = 0; i < 2; i++)
        {
            using var response = await engine.Client.GetAsync("gw/bad");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("""{"error":"bad input"}""", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(calls + 2, Http.Count("https://upstream.test/bad"));
    }

    [Fact]
    public async Task Gateway_APost_IsForwardedEvenWhenAGetIsCached()
    {
        Http.Map("https://upstream.test/items", request => FakeHttpServer.Text(request.Method.Method));
        using (var get = await engine.Client.GetAsync("gw/items"))
            Assert.Equal("GET", await get.Content.ReadAsStringAsync());

        using var post = await engine.Client.PostAsync("gw/items", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal("POST", await post.Content.ReadAsStringAsync());
        Assert.Equal(HttpMethod.Post, Http.Requests.Last(r => r.Uri.AbsolutePath == "/items").Method);
    }

    private async Task<FakeHttpServer.Request> CallGatewayAsync(string path)
    {
        Http.Map("https://upstream.test/headers", _ => FakeHttpServer.Text("ok"));
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Drop", "caller");
        request.Headers.Add("X-Global-Drop", "caller");
        request.Headers.Add("X-One", "caller");
        var before = Http.Requests.Count;

        using var response = await engine.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(Http.Requests.Skip(before), r => r.Uri.Host == "upstream.test");
    }

    [Fact]
    public async Task Gateway_AppliedAndExcludedHeaders_OnTheRoute()
    {
        var sent = await CallGatewayAsync("gw_applied");

        Assert.Equal("1", sent.Headers["X-One"]); // the applied value replaces the caller's
        Assert.False(sent.Headers.ContainsKey("X-Drop"));
        Assert.Equal("caller", sent.Headers["X-Global-Drop"]); // the route's own list replaces the global one
    }

    [Fact]
    public async Task Gateway_AHeaderNameWithControlCharacters_StaysOnOneLogLine()
    {
        // The server accepts a vertical tab and ESC in a header name; the gateway can't forward it and logs it.
        Http.Map("https://upstream.test/headers", _ => FakeHttpServer.Text("ok"));

        var context = await engine.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/gw_applied_named";
            c.Request.Headers["X\u000BForged\u001B[2K"] = "v";
        });

        Assert.Equal(200, context.Response.StatusCode);
        var entry = Assert.Single(engine.Logs.Entries, e => e.Message.Contains("Forged"));
        Assert.Contains("Failed to add header `X%0BForged%1B[2K`", entry.Message);
    }

    [Theory]
    [InlineData("gw_applied_named", "X-Api-Key", "k")]
    [InlineData("gw_applied_named", "Accept", "application/json")]
    [InlineData("gw_applied_repeated", "X-A", "a")]
    [InlineData("gw_applied_repeated", "X-B", "b")]
    public async Task Gateway_SeveralAppliedHeaders_AreAllSent(string path, string header, string value)
    {
        var sent = await CallGatewayAsync(path);

        Assert.Equal(value, sent.Headers[header]);
    }

    [Fact]
    public async Task Gateway_ARouteWithoutExcludedHeaders_UsesTheGlobalList()
    {
        var sent = await CallGatewayAsync("gw_global_excluded");

        Assert.False(sent.Headers.ContainsKey("X-Global-Drop"));
        Assert.Equal("caller", sent.Headers["X-Drop"]);
    }

    private async Task<FakeHttpServer.Request> CallEchoAsync(string requestBody)
    {
        Http.Map("https://echo.test/echo", _ => FakeHttpServer.Json(new { r = "reached" }));
        var before = Http.Requests.Count;

        using var response = await engine.Client.PostAsync("http_marker", new StringContent(requestBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var made = Http.Requests.Skip(before).ToList();
        return Assert.Single(made, r => r.Uri.Host == "echo.test");
    }

    [Fact]
    public async Task HttpMarker_AStringValue_CantRedirectTheCall()
    {
        const string payload = "1, \"url\": \"https://evil.test/\"";

        var call = await CallEchoAsync(JsonSerializer.Serialize(new { payload }));

        Assert.Equal("https://echo.test/echo", call.Uri.AbsoluteUri);
        Assert.Equal(payload, JsonDocument.Parse(call.Body!).RootElement.GetString());
    }

    [Theory]
    [InlineData("http_chain_skip")]
    [InlineData("http_chain_no_wait")]
    public async Task HttpMarker_SkippedOrNoWaitInALaterQuery_IsNull_NotAnEarlierQuerysResponse(string route)
    {
        // Each query used to number its calls from 1, so the later query's empty slot took the
        // earlier query's response.
        Http.Map("https://later.test/first", _ => FakeHttpServer.Json(new { r = "first" }));
        Http.Map("https://later.test/background", _ => FakeHttpServer.Json(new { r = "background" }));
        var backgroundCalls = Http.Count("https://later.test/background");

        using var response = await engine.Client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"second":null}""", await response.Content.ReadAsStringAsync());

        if (route == "http_chain_no_wait")
        {
            // The no_wait call runs in the background: wait until it has been made, so it can't land in a
            // later test of this class.
            for (var i = 0; i < 200 && Http.Count("https://later.test/background") == backgroundCalls; i++)
                await Task.Delay(25);
            Assert.Equal(backgroundCalls + 1, Http.Count("https://later.test/background"));
        }
    }

    [Fact]
    public async Task HttpMarker_SkippedInTheMainQuery_IsNull_NotTheCountQuerysResponse()
    {
        Http.Map("https://later.test/first", _ => FakeHttpServer.Json(new { r = "first" }));

        using var response = await engine.Client.GetAsync("http_count_skip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"success":true,"count":1,"data":[{"second":null}]}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HttpMarker_OneNameUnderTwoMarkers_FillsBoth()
    {
        Http.Map("https://two.test/a/7/b/7", _ => FakeHttpServer.Json(new { r = "reached" }));

        using var response = await engine.Client.PostAsync("http_two_markers",
            new StringContent("""{"id": "7"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/a/7/b/7", Http.Requests.Last(r => r.Uri.Host == "two.test").Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("http_pq_doc", "{{other}}")]
    [InlineData("http_pq_doc_pipes", "||other||")]
    public async Task HttpMarker_MarkersInsideAnEarlierQuerysDocument_StayAsWritten(string route, string otherMarker)
    {
        Http.Map("https://echo.test/echo", _ => FakeHttpServer.Json(new { r = "reached" }));
        var note = JsonSerializer.Serialize(new { s = "{s{secret_key}}", o = otherMarker });
        var before = Http.Requests.Count;

        using var response = await engine.Client.PostAsync(route, new StringContent(
            JsonSerializer.Serialize(new { note, other = "caller-other" }), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = JsonDocument.Parse(Assert.Single(Http.Requests.Skip(before), r => r.Uri.Host == "echo.test").Body!).RootElement;
        Assert.Equal("{s{secret_key}}", sent.GetProperty("s").GetString());
        Assert.Equal(otherMarker, sent.GetProperty("o").GetString());
    }

    [Fact]
    public async Task HttpMarker_AJsonObject_PassesThroughAsJson()
    {
        var call = await CallEchoAsync("""{"payload": {"a": 7, "flag": false}}""");

        var sent = JsonDocument.Parse(call.Body!).RootElement;
        Assert.Equal(7, sent.GetProperty("a").GetInt32());
        Assert.False(sent.GetProperty("flag").GetBoolean());
    }
}
