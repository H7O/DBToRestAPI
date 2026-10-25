namespace DBToRestAPI.Tests;

/// <summary>
/// CORS headers on query and gateway routes, through the whole pipeline. The shipped settings.xml
/// has a global pattern (localhost and *.example.com) and no fallback.
/// </summary>
public class CorsPipelineTests(CorsPipelineTests.CorsEngine engine) : IClassFixture<CorsPipelineTests.CorsEngine>
{
    public sealed class CorsEngine() : PipelineTests.PipelineEngine(_ => new()
    {
        // A root <authorize>, as the shipped auth_providers.xml has: it holds the providers and protects no
        // route, so it turns credentials on for none either.
        ["authorize:providers:cors_test:authority"] = "https://login.example.com",

        ["queries:cors_protected:route"] = "cors/protected",
        ["queries:cors_protected:authorize:provider"] = "cors_test",
        ["queries:cors_protected:query"] = "SELECT 1 AS a;",

        ["queries:cors_protected_hint:route"] = "cors/protected_hint",
        ["queries:cors_protected_hint:authorize:provider"] = "cors_test",
        ["queries:cors_protected_hint:authorize:provider_hint_header"] = "X-Login-With",
        ["queries:cors_protected_hint:query"] = "SELECT 1 AS a;",

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

    [Theory]
    [InlineData("GET")]
    [InlineData("OPTIONS")]
    public async Task ARouteWithoutItsOwnAuthorize_HasNoCredentials_AndAllowsEveryHeader(string method)
    {
        var context = await SendAsync(method, "/cors/star", "https://app.example.com");

        Assert.False(context.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"));
        Assert.Equal("*", context.Response.Headers.AccessControlAllowHeaders.ToString());
    }

    [Theory]
    [InlineData("GET", "/cors/protected", "X-Auth-Provider")]
    [InlineData("OPTIONS", "/cors/protected", "X-Auth-Provider")]
    [InlineData("GET", "/cors/protected_hint", "X-Login-With")]
    [InlineData("OPTIONS", "/cors/protected_hint", "X-Login-With")]
    public async Task ARouteWithItsOwnAuthorize_AllowsCredentials_AndItsProviderHintHeader(string method, string path, string hint)
    {
        var context = await SendAsync(method, path, "https://app.example.com");

        Assert.Equal("true", context.Response.Headers.AccessControlAllowCredentials.ToString());
        Assert.Equal($"Authorization, Content-Type, X-Requested-With, Accept, Origin, X-Api-Key, {hint}",
            context.Response.Headers.AccessControlAllowHeaders.ToString());
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

/// <summary>
/// The global provider_hint_header, under the root &lt;authorize&gt;, names the header a route's own
/// &lt;authorize&gt; adds to the default allowed headers, as it names the header Step 4 reads.
/// </summary>
public class CorsGlobalHintHeaderTests(CorsGlobalHintHeaderTests.Engine engine) : IClassFixture<CorsGlobalHintHeaderTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["authorize:providers:cors_test:authority"] = "https://login.example.com",
        ["authorize:provider_hint_header"] = "X-Which-Idp",

        ["queries:cors_protected:route"] = "cors/protected",
        ["queries:cors_protected:authorize:provider"] = "cors_test",
        ["queries:cors_protected:query"] = "SELECT 1 AS a;",
    });

    [Fact]
    public async Task APreflight_AllowsTheGlobalHintHeader()
    {
        var context = await engine.Server.SendAsync(c =>
        {
            c.Request.Method = "OPTIONS";
            c.Request.Path = "/cors/protected";
            c.Request.Headers.Origin = "https://app.example.com";
            c.Request.Headers.AccessControlRequestMethod = "GET";
        });

        Assert.Equal("Authorization, Content-Type, X-Requested-With, Accept, Origin, X-Api-Key, X-Which-Idp",
            context.Response.Headers.AccessControlAllowHeaders.ToString());
    }
}
