namespace DBToRestAPI.Tests;

/// <summary>
/// CORS headers on query and gateway routes, through the whole pipeline. The shipped settings.xml
/// has a global pattern (localhost and *.example.com) and no fallback.
/// </summary>
public class CorsPipelineTests(CorsPipelineTests.CorsEngine engine) : IClassFixture<CorsPipelineTests.CorsEngine>
{
    public sealed class CorsEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        // A root <authorize> turns credentials on for every route, as the shipped auth_providers.xml does.
        ["authorize:providers:cors_test:authority"] = "https://login.example.com",

        ["queries:cors_star:route"] = "cors/star",
        ["queries:cors_star:cors:fallback_origin"] = "*",
        ["queries:cors_star:query"] = "SELECT 1 AS a;",

        ["queries:cors_no_credentials:route"] = "cors/no_credentials",
        ["queries:cors_no_credentials:cors:allow_credentials"] = "false",
        ["queries:cors_no_credentials:query"] = "SELECT 1 AS a;",

        ["queries:cors_exposed:route"] = "cors/exposed",
        ["queries:cors_exposed:cors:exposed_headers"] = "X-Total-Count, Content-Disposition",
        ["queries:cors_exposed:query"] = "SELECT 1 AS a;",

        ["routes:cors_gateway:route"] = "cors/gateway",
        ["routes:cors_gateway:url"] = "https://upstream.example/data",
    });

    private Task<Microsoft.AspNetCore.Http.HttpContext> SendAsync(string method, string path, string origin)
        => engine.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Request.Headers.Origin = origin;
            if (method == "OPTIONS")
                c.Request.Headers.AccessControlRequestMethod = "GET";
        });

    [Theory]
    [InlineData("GET")]
    [InlineData("OPTIONS")]
    public async Task AStarFallback_AllowsEveryOrigin(string method)
    {
        var context = await SendAsync(method, "/cors/star", "https://other.example.org");

        Assert.Equal("*", context.Response.Headers.AccessControlAllowOrigin.ToString());
    }

    [Fact]
    public async Task APreflight_KeepsARoutesAllowCredentialsFalse_UnderARootAuthorize()
    {
        var get = await SendAsync("GET", "/cors/no_credentials", "https://app.example.com");
        var preflight = await SendAsync("OPTIONS", "/cors/no_credentials", "https://app.example.com");

        Assert.Equal(204, preflight.Response.StatusCode);
        Assert.False(get.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"));
        Assert.False(preflight.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task APreflight_UnderARootAuthorize_AllowsCredentials()
    {
        var preflight = await SendAsync("OPTIONS", "/cors/star", "https://app.example.com");

        Assert.Equal("true", preflight.Response.Headers.AccessControlAllowCredentials.ToString());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("OPTIONS")]
    public async Task AnAnswerThatDependsOnTheOrigin_SaysSo(string method)
    {
        var context = await SendAsync(method, "/cors/no_credentials", "https://app.example.com");

        Assert.Equal("https://app.example.com", context.Response.Headers.AccessControlAllowOrigin.ToString());
        Assert.Contains("Origin", context.Response.Headers.Vary.ToString());
    }

    [Fact]
    public async Task APreflightToAGatewayRoute_IsAnswered()
    {
        var context = await SendAsync("OPTIONS", "/cors/gateway", "https://app.example.com");

        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal("https://app.example.com", context.Response.Headers.AccessControlAllowOrigin.ToString());
        Assert.Contains("GET", context.Response.Headers.AccessControlAllowMethods.ToString());
    }

    [Theory]
    [InlineData("/cors/star", "Content-Disposition, Retry-After")]
    [InlineData("/cors/exposed", "X-Total-Count, Content-Disposition")]
    [InlineData("/cors/gateway", "Content-Disposition, Retry-After")]
    public async Task AnAnswer_LetsThePageRead_ADownloadsFileName_AndA429sWait(string path, string exposed)
    {
        var context = await SendAsync("GET", path, "https://app.example.com");

        Assert.Equal(exposed, context.Response.Headers.AccessControlExposeHeaders.ToString());
    }

    [Fact]
    public async Task APreflight_ExposesNothing()
    {
        var context = await SendAsync("OPTIONS", "/cors/exposed", "https://app.example.com");

        Assert.False(context.Response.Headers.ContainsKey("Access-Control-Expose-Headers"));
    }
}
