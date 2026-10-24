using System.Net;
using System.Text;
using System.Text.Json;
using DBToRestAPI.Middlewares;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DBToRestAPI.Tests;

/// <summary>
/// A body is read as JSON (`application/json`, `text/json`, `application/*+json`, or no Content-Type)
/// or as a form. A body of any other type is refused with 415: it used to be read as no parameters, so
/// the query ran with every body parameter NULL and the request succeeded. A route's path is looked up
/// as sent, so `json/` is no prefix.
/// </summary>
public class RequestBodyTypeTests(RequestBodyTypeTests.Engine engine) : IClassFixture<RequestBodyTypeTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:body_name:route"] = "body/name",
        ["queries:body_name:verb"] = "GET,POST",
        ["queries:body_name:query"] = "SELECT {{name}} AS name;",

        ["queries:body_insert:route"] = "body/insert",
        ["queries:body_insert:verb"] = "POST",
        ["queries:body_insert:query"] = "INSERT INTO uploads (path) VALUES ({{marker}}); SELECT 1 AS done;",

        ["queries:body_count:route"] = "body/count",
        ["queries:body_count:verb"] = "GET",
        ["queries:body_count:query"] = "SELECT count(*) AS n FROM uploads WHERE path = {{marker}};",

        ["queries:json_orders:route"] = "json/orders",
        ["queries:json_orders:verb"] = "GET",
        ["queries:json_orders:query"] = "SELECT 'json/orders' AS route;",

        ["routes:body_gw:route"] = "body/gw/*",
        ["routes:body_gw:url"] = "https://upstream.test/",
    });

    /// <summary>A body with exactly this Content-Type header, or none.</summary>
    internal static HttpContent Body(string text, string? contentType)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        if (contentType != null)
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return content;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await engine.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Theory]
    [InlineData(null, "application/json")]
    [InlineData("", "application/json")]
    [InlineData(" ; charset=utf-8", "application/json")]
    [InlineData("Application/JSON; charset=utf-8", "application/json")]
    [InlineData("text/json", "application/json")]
    [InlineData("application/problem+json", "application/json")]
    [InlineData("multipart/form-data; boundary=x", "multipart/form-data")]
    [InlineData("Application/X-WWW-Form-Urlencoded", "application/x-www-form-urlencoded")]
    [InlineData("text/plain; charset=utf-8", "text/plain")]
    [InlineData("application/json-seq", "application/json-seq")]
    [InlineData("text/plain+json", "text/plain+json")]
    public void TheBodysType_IsItsMediaType_WithEveryJsonTypeAsJson(string? header, string expected)
    {
        Assert.Equal(expected, Step1ServiceTypeChecks.BodyType(header));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("text/json")]
    [InlineData("application/vnd.api+json")]
    [InlineData("application/merge-patch+json")]
    [InlineData(null)]
    public async Task AJsonBody_IsRead(string? contentType)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "body/name", Body("""{"name":"ann"}""", contentType));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ann", Json(body).GetProperty("name").GetString());
    }

    [Fact]
    public async Task AFormBody_IsRead()
    {
        var urlEncoded = await SendAsync(HttpMethod.Post, "body/name", new FormUrlEncodedContent([new("name", "ann")]));
        var multipart = await SendAsync(HttpMethod.Post, "body/name", new MultipartFormDataContent { { new StringContent("bob"), "name" } });

        Assert.Equal(HttpStatusCode.OK, urlEncoded.Status);
        Assert.Equal("ann", Json(urlEncoded.Body).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, multipart.Status);
        Assert.Equal("bob", Json(multipart.Body).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("text/plain", "text/plain")]
    [InlineData("text/plain; charset=utf-8", "text/plain")]
    [InlineData("application/xml", "application/xml")]
    [InlineData("application/octet-stream", "application/octet-stream")]
    [InlineData("application/x-ndjson", "application/x-ndjson")]
    public async Task ABodyOfAnotherType_Is415_AndTheQueryDoesntRun(string contentType, string named)
    {
        var marker = Guid.NewGuid().ToString("N");

        var (status, body) = await SendAsync(HttpMethod.Post, "body/insert", Body($$"""{"marker":"{{marker}}"}""", contentType));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        Assert.False(Json(body).GetProperty("success").GetBoolean());
        Assert.Equal(
            $"A request body of type `{named}` can't be read. Send JSON as `application/json`, "
            + "or a form as `multipart/form-data` or `application/x-www-form-urlencoded`.",
            Json(body).GetProperty("message").GetString());
        var (_, count) = await SendAsync(HttpMethod.Get, $"body/count?marker={marker}");
        Assert.Equal(0, Json(count).GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task TheSameBodyAsJson_RunsTheQuery()
    {
        var marker = Guid.NewGuid().ToString("N");

        var (status, _) = await SendAsync(HttpMethod.Post, "body/insert", Body($$"""{"marker":"{{marker}}"}""", "application/json"));

        Assert.Equal(HttpStatusCode.OK, status);
        var (_, count) = await SendAsync(HttpMethod.Get, $"body/count?marker={marker}");
        Assert.Equal(1, Json(count).GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task ARequestOfAnotherTypeWithoutABody_IsRead()
    {
        // Some clients send a Content-Type on every request. Without a body there is nothing to misread.
        var (status, body) = await SendAsync(HttpMethod.Get, "body/name?name=bob", Body("", "text/plain"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("bob", Json(body).GetProperty("name").GetString());
    }

    [Fact]
    public async Task The415_CarriesTheCorsHeaders()
    {
        // Answered after the CORS step, so a page on another origin can read why it was refused.
        using var request = new HttpRequestMessage(HttpMethod.Post, "body/name") { Content = Body("""{"name":"ann"}""", "text/plain") };
        request.Headers.Add("Origin", "https://app.example.com");

        using var response = await engine.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("https://app.example.com", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task AGatewayRoute_ForwardsABodyOfAnotherTypeAsSent()
    {
        engine.Http.Map("https://upstream.test/echo", request => FakeHttpServer.Text(request.Body ?? ""));

        var (status, body) = await SendAsync(HttpMethod.Post, "body/gw/echo", Body("plain words", "text/plain"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("plain words", body);
        var forwarded = engine.Http.Requests.Last(r => r.Uri.AbsolutePath == "/echo");
        Assert.Equal("text/plain", forwarded.Headers["Content-Type"]);
    }

    [Fact]
    public async Task ARouteThatStartsWithJson_IsReachable()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "json/orders");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("json/orders", Json(body).GetProperty("route").GetString());
    }

    [Fact]
    public async Task JsonBeforeARoute_IsPartOfThePath()
    {
        var (status, _) = await SendAsync(HttpMethod.Get, "json/body/name?name=bob");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}

/// <summary>
/// An app that calls `/json/&lt;route&gt;` and sends its JSON under another type, or none: a gateway
/// entry forwards each such call to the engine's own `&lt;route&gt;`, with the body's type set to JSON.
/// The entry is read from the XML that would sit in api_gateway.xml.
/// </summary>
public class LegacyJsonPrefixGatewayTests(LegacyJsonPrefixGatewayTests.Engine engine) : IClassFixture<LegacyJsonPrefixGatewayTests.Engine>
{
    private const string Entry = """
        <settings>
          <routes>
            <legacy_json_prefix>
              <route>json/*</route>
              <url>http://engine.invalid/</url>
              <excluded_headers>host</excluded_headers>
              <applied_headers>
                <header>
                  <name>Content-Type</name>
                  <value>application/json</value>
                </header>
              </applied_headers>
            </legacy_json_prefix>
          </routes>
        </settings>
        """;

    public sealed class Engine : PipelineTests.PipelineEngine
    {
        public Engine() : base(_ => Settings(), kestrel: true, fakeHttp: false)
        {
            // The engine's own address, known once it listens. Set where the gateway reads it.
            Services.GetRequiredService<DBToRestAPI.Services.IEncryptedConfiguration>()["routes:legacy_json_prefix:url"] = Client.BaseAddress!.ToString();
        }

        private static Dictionary<string, string?> Settings()
        {
            var settings = new ConfigurationBuilder()
                .AddXmlStream(new MemoryStream(Encoding.UTF8.GetBytes(Entry)))
                .Build()
                .AsEnumerable()
                .Where(pair => pair.Value != null)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            settings["queries:body_name:route"] = "body/name";
            settings["queries:body_name:verb"] = "GET,POST";
            settings["queries:body_name:query"] = "SELECT {{name}} AS name;";
            return settings;
        }
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData(null)]
    [InlineData("application/json")]
    public async Task ABodyUnderTheJsonPath_ReachesTheRouteAsJson(string? contentType)
    {
        using var response = await engine.Client.PostAsync("json/body/name", RequestBodyTypeTests.Body("""{"name":"ann"}""", contentType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ann", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AGetUnderTheJsonPath_KeepsItsQueryString()
    {
        using var response = await engine.Client.GetAsync("json/body/name?name=bob");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bob", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheRouteItself_RefusesABodyOfAnotherType()
    {
        using var response = await engine.Client.PostAsync("body/name", RequestBodyTypeTests.Body("""{"name":"ann"}""", "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
