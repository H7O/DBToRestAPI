using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// The whole engine on a real socket (Kestrel on a free loopback port), for what the in-memory
/// test server can't show: how a client sees a response that is cut after part of it was sent.
/// </summary>
public class KestrelPipelineTests(KestrelPipelineTests.KestrelEngine engine, ITestOutputHelper output)
    : IClassFixture<KestrelPipelineTests.KestrelEngine>
{
    public sealed class KestrelEngine() : PipelineTests.PipelineEngine(null, kestrel: true);

    [Fact]
    public async Task ServesOnALoopbackSocket()
    {
        Assert.Equal("127.0.0.1", engine.Client.BaseAddress?.Host);
        Assert.NotEqual(80, engine.Client.BaseAddress?.Port);

        using var response = await engine.Client.GetAsync("pl/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""[{"a":1},{"a":2},{"a":3}]""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AGetWithoutABody_ParsesNoJson()
    {
        // A request without a Content-Type counts as JSON. Its empty body used to be parsed, which threw
        // and caught a JsonException on every GET.
        using var response = await engine.Client.GetAsync("pl/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(engine.Logs.Entries, e => e.Exception is JsonException);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(2000)]
    [InlineData(20000)]
    public async Task ErrorAfterRows_IsNeverASuccessOrABrokenBody(int rows)
    {
        // Before any of the result has been sent, the client gets the clean mapped error. After,
        // the status can't change and the engine cuts the connection, which the client sees as
        // a failed read. Never a 200, and never the error status with rows in front of its body.
        HttpResponseMessage response;
        string body;
        try
        {
            response = await engine.Client.GetAsync($"pl/window/{rows}");
            body = await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            output.WriteLine($"{rows} rows: connection cut ({ex.InnerException?.GetType().Name ?? ex.GetType().Name})");
            return;
        }
        output.WriteLine($"{rows} rows: {(int)response.StatusCode}");

        using (response)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var json = JsonDocument.Parse(body).RootElement;
            Assert.Equal(404, json.GetProperty("error_number").GetInt32());
            Assert.Contains("Category not found", json.GetProperty("message").GetString());
        }
    }
}
