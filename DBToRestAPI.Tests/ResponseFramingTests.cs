using System.Net;
using System.Text;
using System.Text.Json;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// How <c>response_prefix</c> and <c>response_suffix</c> are read: the three encodings, a route's tag
/// against the global one, and a value that can't be decoded.
/// </summary>
public class ResponseFramingIndexTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static ResponseFramingIndex Index(IConfigurationRoot config, ILogger<ResponseFramingIndex>? logger = null)
        => new(new TestEncryptedConfiguration(config), logger ?? NullLogger<ResponseFramingIndex>.Instance);

    private static IConfigurationRoot Settings(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>The configuration an XML settings file gives, as the engine reads one.</summary>
    private static IConfigurationRoot Xml(string xml)
        => new ConfigurationBuilder().AddXmlStream(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Build();

    private static IConfigurationSection Route(IConfigurationRoot config, string name) => config.GetSection("queries:" + name);

    [Fact]
    public void NoTags_IsNotInUse_AndEveryRouteGetsNothing()
    {
        var config = Settings(new() { ["queries:a:route"] = "a" });
        var index = Index(config);

        Assert.False(index.InUse);
        Assert.True(index.For(Route(config, "a")).IsNone);
        Assert.True(index.For(null).IsNone);
    }

    [Theory]
    [InlineData(null, "abc", "616263")]
    [InlineData("text", "é", "C3A9")]
    [InlineData("hex", "EF BB BF", "EFBBBF")]
    [InlineData("hex", "ef\n\tbb  bf", "EFBBBF")]           // whitespace and line breaks are ignored
    [InlineData("HEX", "0d0a", "0D0A")]                      // the encoding's case doesn't matter
    [InlineData("base64", "77u/", "EFBBBF")]
    [InlineData("base64", " 77 u/ ", "EFBBBF")]
    public void EachEncoding_GivesItsBytes(string? encoding, string value, string expectedHex)
    {
        var settings = new Dictionary<string, string?> { ["queries:a:response_prefix"] = value };
        if (encoding is not null)
            settings["queries:a:response_prefix:encoding"] = encoding;
        var config = Settings(settings);

        var framing = Index(config).For(Route(config, "a"));

        Assert.Equal(expectedHex, Convert.ToHexString(framing.Prefix));
        Assert.Empty(framing.Suffix);
        Assert.Null(framing.Error);
    }

    [Fact]
    public void FromXml_AnAttributeAndACharacterReference_AreRead()
    {
        var config = Xml("""
            <settings>
              <response_prefix encoding="hex">EF BB BF</response_prefix>
              <queries>
                <text_bom><route>t</route><response_prefix>&#xFEFF;</response_prefix></text_bom>
                <off><route>o</route><response_prefix encoding="hex"/></off>
              </queries>
            </settings>
            """);
        var index = Index(config);

        Assert.Equal(Bom, index.For(null).Prefix);
        // &#xFEFF; is the byte order mark's character, which UTF-8 writes as EF BB BF.
        Assert.Equal(Bom, index.For(Route(config, "text_bom")).Prefix);
        // A tag with only its attribute is an empty tag: it turns the global prefix off.
        Assert.Empty(index.For(Route(config, "off")).Prefix);
    }

    [Fact]
    public void ARoutesTag_ReplacesTheGlobalOne_AndThePrefixAndSuffixAreInheritedSeparately()
    {
        var config = Settings(new()
        {
            ["response_prefix"] = "<",
            ["response_suffix"] = ">",
            ["queries:own_prefix:response_prefix"] = "[",
            ["queries:no_prefix:response_prefix"] = "",
            ["queries:own_suffix:response_suffix"] = "]",
            ["queries:plain:route"] = "plain",
        });
        var index = Index(config);

        Assert.True(index.InUse);
        Assert.Equal("[>", Text(index.For(Route(config, "own_prefix"))));
        Assert.Equal(">", Text(index.For(Route(config, "no_prefix"))));
        Assert.Equal("<]", Text(index.For(Route(config, "own_suffix"))));
        // A route without either tag isn't in the index, and gets the global ones.
        Assert.Equal("<>", Text(index.For(Route(config, "plain"))));
    }

    private static string Text(ResponseFraming framing)
        => Encoding.UTF8.GetString(framing.Prefix) + Encoding.UTF8.GetString(framing.Suffix);

    [Theory]
    [InlineData("hex", "ZZ", "isn't hex")]
    [InlineData("hex", "ABC", "isn't hex")]                  // an odd number of digits
    [InlineData("base64", "@@@", "isn't base64")]
    [InlineData("rot13", "x", "isn't text, hex or base64")]
    public void AValueThatCantBeDecoded_StopsTheStart_NamingTheRoute(string encoding, string value, string expected)
    {
        var config = Settings(new()
        {
            ["queries:broken:response_suffix"] = value,
            ["queries:broken:response_suffix:encoding"] = encoding,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => Index(config));

        Assert.Contains("queries:broken", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void AfterAReload_ABadValue_MarksOnlyItsRoute_AndIsLogged()
    {
        var config = Settings(new()
        {
            ["queries:good:response_prefix"] = "ok",
            ["queries:broken:response_prefix"] = "ok",
        });
        var logs = new CapturedLogs();
        var index = Index(config, new Logger<ResponseFramingIndex>(LoggerFactory.Create(b => b.AddProvider(logs))));

        config["queries:broken:response_prefix:encoding"] = "hex";
        config["queries:broken:response_prefix"] = "not hex";
        config.Reload();

        Assert.Contains("isn't hex", index.For(Route(config, "broken")).Error);
        Assert.Null(index.For(Route(config, "good")).Error);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("queries:broken"));

        // Fixed, the route is framed again.
        config["queries:broken:response_prefix"] = "EF BB BF";
        config.Reload();
        Assert.Equal(Bom, index.For(Route(config, "broken")).Prefix);
        Assert.Null(index.For(Route(config, "broken")).Error);
    }

    [Fact]
    public void AGlobalBadValue_ReachesARouteThatTakesThatTag_NotOneThatReplacesIt()
    {
        var config = Settings(new()
        {
            ["response_prefix"] = "x",
            ["queries:takes_it:response_suffix"] = "!",
            ["queries:replaces_it:response_prefix"] = "[",
            ["queries:replaces_it:response_suffix"] = "]",
        });
        var index = Index(config);

        config["response_prefix:encoding"] = "hex";
        config.Reload();

        Assert.NotNull(index.For(Route(config, "takes_it")).Error);
        Assert.Null(index.For(Route(config, "replaces_it")).Error);
    }
}

/// <summary>
/// The body wrapper on its own: each way of writing a body gets the prefix first and the suffix last,
/// and an answer bytes can't be added to passes through unchanged.
/// </summary>
public class ResponseFramingBodyTests
{
    private static readonly ResponseFraming Framing = new(Encoding.UTF8.GetBytes("<<"), Encoding.UTF8.GetBytes(">"), null);

    private static (ResponseFramingBody Body, MemoryStream Sent, HttpContext Context) Create(
        ResponseFraming? framing = null, Action<HttpContext>? setUp = null)
    {
        var context = new DefaultHttpContext();
        setUp?.Invoke(context);
        var sent = new MemoryStream();
        var body = new ResponseFramingBody(new StreamResponseBodyFeature(sent), context.Response, framing ?? Framing);
        return (body, sent, context);
    }

    private static string Text(MemoryStream sent) => Encoding.UTF8.GetString(sent.ToArray());

    [Fact]
    public async Task StreamWrites_GetThePrefixOnce_AndTheSuffixAtTheEnd()
    {
        var (body, sent, _) = Create();
        Stream stream = ((IHttpResponseBodyFeature)body).Stream;

        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"a\":"));
        await stream.WriteAsync(ReadOnlyMemory<byte>.Empty);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("1}"));
        await body.FinishAsync();
        await body.FinishAsync();

        Assert.Equal("<<{\"a\":1}>", Text(sent));
    }

    [Fact]
    public async Task ASynchronousWrite_GetsThePrefixToo()
    {
        var (body, sent, _) = Create();

        ((IHttpResponseBodyFeature)body).Stream.Write(Encoding.UTF8.GetBytes("x"), 0, 1);
        await body.FinishAsync();

        Assert.Equal("<<x>", Text(sent));
    }

    [Fact]
    public async Task ThePipeWriter_AsSystemTextJsonUsesIt_IsFramed()
    {
        var (body, sent, _) = Create();

        using (var json = new Utf8JsonWriter(body.Writer))
        {
            json.WriteStartObject();
            json.WriteNumber("n", 1);
            json.WriteEndObject();
        }
        await body.FinishAsync();

        Assert.Equal("<<{\"n\":1}>", Text(sent));
    }

    [Fact]
    public async Task SendFile_GoesThroughTheWrapper()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "0123456789");
            var (body, sent, _) = Create();

            await body.SendFileAsync(path, 2, 3);
            await body.CompleteAsync();

            Assert.Equal("<<234>", Text(sent));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NoBody_GetsNeitherThePrefixNorTheSuffix()
    {
        var (body, sent, _) = Create(setUp: c => c.Response.StatusCode = 204);

        await body.FinishAsync();

        Assert.Equal(0, sent.Length);
    }

    [Fact]
    public async Task AnEmptyPrefix_StillOwesTheSuffix()
    {
        var (body, sent, _) = Create(new ResponseFraming([], Encoding.UTF8.GetBytes(">"), null));

        await ((IHttpResponseBodyFeature)body).Stream.WriteAsync(Encoding.UTF8.GetBytes("x"));
        await body.FinishAsync();

        Assert.Equal("x>", Text(sent));
    }

    [Theory]
    [InlineData(206, null)]
    [InlineData(200, "gzip")]
    public async Task APartialOrCompressedAnswer_PassesThroughUnchanged(int status, string? contentEncoding)
    {
        var (body, sent, context) = Create(setUp: c =>
        {
            c.Response.StatusCode = status;
            c.Response.ContentLength = 3;
            if (contentEncoding is not null) c.Response.Headers.ContentEncoding = contentEncoding;
        });

        body.AdjustContentLength();
        await ((IHttpResponseBodyFeature)body).Stream.WriteAsync(Encoding.UTF8.GetBytes("abc"));
        await body.FinishAsync();

        Assert.Equal("abc", Text(sent));
        Assert.Equal(3, context.Response.ContentLength);
    }

    [Fact]
    public void AContentLength_IsIncreasedByThePrefixAndTheSuffix()
    {
        var (body, _, context) = Create(setUp: c => c.Response.ContentLength = 10);

        body.AdjustContentLength();

        Assert.Equal(13, context.Response.ContentLength);
    }

    [Fact]
    public async Task ACutConnection_GetsNoSuffix()
    {
        using var aborted = new CancellationTokenSource();
        var (body, sent, _) = Create(setUp: c => c.RequestAborted = aborted.Token);

        await ((IHttpResponseBodyFeature)body).Stream.WriteAsync(Encoding.UTF8.GetBytes("[1,"));
        aborted.Cancel();
        await body.FinishAsync();

        Assert.Equal("<<[1,", Text(sent));
    }
}

/// <summary>
/// Requests through the engine with a global prefix and suffix: every body a route sends is framed,
/// byte for byte, and what has no body, or never reaches a route, is not.
/// </summary>
public class ResponseFramingPipelineTests(ResponseFramingPipelineTests.Engine engine) : IClassFixture<ResponseFramingPipelineTests.Engine>
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public sealed class Engine() : PipelineTests.PipelineEngine(folder =>
    {
        Directory.CreateDirectory(Path.Combine(folder, "store"));
        File.WriteAllText(Path.Combine(folder, "store", "hello.txt"), "hello");
        return new()
        {
            ["response_prefix"] = "EF BB BF",
            ["response_prefix:encoding"] = "hex",
            ["response_suffix"] = "\n",

            ["queries:fr_one:route"] = "fr/one",
            ["queries:fr_one:verb"] = "GET",
            ["queries:fr_one:query"] = "SELECT 1 AS n;",

            ["queries:fr_rows:route"] = "fr/rows",
            ["queries:fr_rows:verb"] = "GET",
            ["queries:fr_rows:response_structure"] = "array",
            ["queries:fr_rows:query"] = "WITH r(n) AS (VALUES (1), (2), (3)) SELECT n FROM r ORDER BY n;",

            ["queries:fr_cached:route"] = "fr/cached",
            ["queries:fr_cached:verb"] = "GET",
            ["queries:fr_cached:cache:memory:duration_in_milliseconds"] = "60000",
            ["queries:fr_cached:query"] = "SELECT random() AS r;",

            // RAISE() in the pipeline database's trigger answers 404.
            ["queries:fr_error:route"] = "fr/error",
            ["queries:fr_error:verb"] = "POST",
            ["queries:fr_error:query"] = "INSERT INTO raise_after VALUES (1);",

            ["queries:fr_own:route"] = "fr/own",
            ["queries:fr_own:verb"] = "GET",
            ["queries:fr_own:response_prefix"] = ")]}'",
            ["queries:fr_own:query"] = "SELECT 1 AS n;",

            ["queries:fr_off:route"] = "fr/off",
            ["queries:fr_off:verb"] = "GET",
            ["queries:fr_off:response_prefix"] = "",
            ["queries:fr_off:response_suffix"] = "",
            ["queries:fr_off:query"] = "SELECT 1 AS n;",

            ["file_management:local_file_store:fr_store:base_path"] = Path.Combine(folder, "store") + Path.DirectorySeparatorChar,
            ["queries:fr_file:route"] = "fr/file",
            ["queries:fr_file:verb"] = "GET",
            ["queries:fr_file:response_structure"] = "file",
            ["queries:fr_file:file_management:store"] = "fr_store",
            ["queries:fr_file:query"] = "SELECT 'hello.txt' AS relative_path, 'hello.txt' AS file_name;",

            ["routes:fr_gateway:route"] = "fr/gateway",
            ["routes:fr_gateway:url"] = "https://framing.test/target",
            ["routes:fr_gzip:route"] = "fr/gzip",
            ["routes:fr_gzip:url"] = "https://framing.test/gzip",
        };
    });

    private async Task<(HttpStatusCode Status, byte[] Body, long? ContentLength)> SendAsync(HttpRequestMessage request)
    {
        using var response = await engine.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentLength);
    }

    private Task<(HttpStatusCode Status, byte[] Body, long? ContentLength)> GetAsync(string path)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    private static byte[] Framed(string json) => [.. Bom, .. Encoding.UTF8.GetBytes(json), (byte)'\n'];

    [Theory]
    [InlineData("fr/one", """{"n":1}""")]
    [InlineData("fr/rows", """[{"n":1},{"n":2},{"n":3}]""")]
    public async Task AQuerysAnswer_IsFramed_ByteForByte(string path, string json)
    {
        var (status, body, _) = await GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Framed(json), body);
    }

    [Fact]
    public async Task ACachedAnswer_IsFramed_OnTheFillAndOnTheHit()
    {
        var (_, fill, _) = await GetAsync("fr/cached");
        var (_, hit, _) = await GetAsync("fr/cached");

        Assert.Equal(fill, hit);
        Assert.Equal(Bom, fill[..3]);
        // The cache holds the plain answer, so a hit isn't framed twice.
        Assert.Equal('\n', (char)hit[^1]);
        Assert.Equal('}', (char)hit[^2]);
    }

    [Fact]
    public async Task AnError_IsFramed()
    {
        var (status, body, _) = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "fr/error")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(Bom, body[..3]);
        using var json = JsonDocument.Parse(body.AsMemory(3, body.Length - 4));
        Assert.Equal("Category not found", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ARoutesOwnPrefix_ReplacesTheGlobalOne_AndEmptyTagsTurnBothOff()
    {
        var (_, own, _) = await GetAsync("fr/own");
        var (_, off, _) = await GetAsync("fr/off");

        Assert.Equal(")]}'{\"n\":1}\n", Encoding.UTF8.GetString(own));
        Assert.Equal("""{"n":1}""", Encoding.UTF8.GetString(off));
    }

    [Fact]
    public async Task AFile_IsFramed_WithItsContentLengthIncreased()
    {
        var (status, body, contentLength) = await GetAsync("fr/file");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Framed("hello"), body);
        Assert.Equal(body.Length, contentLength);
    }

    /// <summary>On Kestrel, which drops a HEAD answer's body as a deployed engine does; the test server doesn't.</summary>
    public sealed class KestrelEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["response_prefix"] = "EF BB BF",
        ["response_prefix:encoding"] = "hex",
        ["response_suffix"] = "0A",
        ["response_suffix:encoding"] = "hex",
        ["queries:fr_one:route"] = "fr/one",
        ["queries:fr_one:verb"] = "GET",
        ["queries:fr_one:query"] = "SELECT 1 AS n;",
    }, kestrel: true);

    [Fact]
    public async Task OnKestrel_AnswersAreFramed_AndAHeadRequestGetsNoBody()
    {
        using var kestrel = new KestrelEngine();

        using (var get = await kestrel.Client.GetAsync("fr/one"))
            Assert.Equal(Framed("""{"n":1}"""), await get.Content.ReadAsByteArrayAsync());

        using var head = await kestrel.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "fr/one"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnUnknownRoute_IsNotFramed()
    {
        // Answered by step 1, before a route was found.
        var (status, body, _) = await GetAsync("fr/no_such_route");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.NotEqual(Bom, body.Take(3).ToArray());
    }

    [Fact]
    public async Task AGatewayAnswer_IsFramed_AndTheTargetIsAskedForTheWholeUncompressedAnswer()
    {
        engine.Http.Map("https://framing.test/target", _ => FakeHttpServer.Json(new { ok = true }));
        var request = new HttpRequestMessage(HttpMethod.Get, "fr/gateway");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

        var (status, body, _) = await SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Framed("""{"ok":true}"""), body);
        var forwarded = engine.Http.Requests.Last(r => r.Uri.AbsolutePath == "/target");
        Assert.False(forwarded.Headers.ContainsKey("Range"));
        Assert.False(forwarded.Headers.ContainsKey("Accept-Encoding"));
    }

    [Fact]
    public async Task ACompressedGatewayAnswer_PassesThroughUnchanged()
    {
        byte[] gzipped = [0x1F, 0x8B, 0x08, 0x00];
        engine.Http.Map("https://framing.test/gzip", _ =>
        {
            var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gzipped) };
            answer.Content.Headers.ContentEncoding.Add("gzip");
            return answer;
        });

        var (status, body, _) = await GetAsync("fr/gzip");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(gzipped, body);
    }

    /// <summary>The same routes without the tags: the step only reads its flag, and the bytes are the plain answer.</summary>
    public sealed class PlainEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:fr_one:route"] = "fr/one",
        ["queries:fr_one:verb"] = "GET",
        ["queries:fr_one:query"] = "SELECT 1 AS n;",
    });

    [Fact]
    public async Task WithoutTheTags_TheAnswerIsThePlainOne()
    {
        using var plain = new PlainEngine();

        Assert.False(plain.Services.GetRequiredService<ResponseFramingIndex>().InUse);
        using var response = await plain.Client.GetAsync("fr/one");
        Assert.Equal("""{"n":1}""", Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()));
    }

    /// <summary>A value that can't be decoded, from the start.</summary>
    public sealed class BrokenEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["response_prefix"] = "not hex",
        ["response_prefix:encoding"] = "hex",
    });

    [Fact]
    public void ABadValueAtStartUp_StopsTheEngine()
    {
        var ex = Record.Exception(() => { using var broken = new BrokenEngine(); });

        Assert.NotNull(ex);
        Assert.Contains("isn't hex", Flatten(ex));
    }

    private static string Flatten(Exception ex)
        => ex.InnerException is null ? ex.Message : ex.Message + " | " + Flatten(ex.InnerException);

    [Fact]
    public async Task ABadValueAfterAReload_AnswersOnlyThatRoute500()
    {
        using var reloaded = new PlainEngine();
        var configuration = (IConfigurationRoot)reloaded.Services.GetRequiredService<IConfiguration>();
        configuration["queries:fr_one:response_prefix:encoding"] = "base64";
        configuration["queries:fr_one:response_prefix"] = "@@@";
        configuration.Reload();

        using var response = await reloaded.Client.GetAsync("fr/one");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("response_prefix", await response.Content.ReadAsStringAsync());
        Assert.Contains(reloaded.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("isn't base64"));
    }
}
