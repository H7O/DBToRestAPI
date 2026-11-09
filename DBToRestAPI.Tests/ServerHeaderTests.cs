using System.Net;

namespace DBToRestAPI.Tests;

/// <summary>
/// An answer has no <c>Server</c> header. Kestrel adds <c>Server: Kestrel</c> unless told not to, so this
/// runs on Kestrel: the in-memory test server never adds one.
/// </summary>
public class ServerHeaderTests(ServerHeaderTests.Engine engine) : IClassFixture<ServerHeaderTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:sh_one:route"] = "sh/one",
        ["queries:sh_one:verb"] = "GET",
        ["queries:sh_one:query"] = "SELECT 1 AS n;",
    }, kestrel: true);

    [Theory]
    [InlineData("sh/one", HttpStatusCode.OK)]
    [InlineData("sh/no_such_route", HttpStatusCode.NotFound)]
    public async Task AnAnswer_HasNoServerHeader(string path, HttpStatusCode status)
    {
        using var response = await engine.Client.GetAsync(path);

        Assert.Equal(status, response.StatusCode);
        Assert.Empty(response.Headers.Server);
    }
}
