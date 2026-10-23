using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DBToRestAPI.Tests;

/// <summary>
/// docs/topics/07-caching.md, "Cache Key Components": an invalidator's value is read from each
/// source separately, a `&lt;vars&gt;` key and a token claim included, so a name that a var or a
/// claim shares only splits entries further. The caller's identity separates entries only when its
/// claim is named in &lt;invalidators&gt;. Each route answers a random number, so a cache hit
/// answers the number an earlier request got.
/// </summary>
public class CacheInvalidatorSourcesTests(CacheInvalidatorSourcesTests.Engine engine) : IClassFixture<CacheInvalidatorSourcesTests.Engine>
{
    public sealed class Engine : PipelineTests.PipelineEngine
    {
        public FakeIdp Idp { get; }

        public Engine() : base(_ => new()
        {
            ["authorize:providers:idp:authority"] = "https://idp.test",
            ["authorize:providers:idp:audience"] = "api://cache",
            // A var with an invalidator's name
            ["vars:category"] = "constant",

            ["queries:by_category:route"] = "cache/by-category",
            ["queries:by_category:verb"] = "GET",
            ["queries:by_category:authorize:provider"] = "idp",
            ["queries:by_category:cache:memory:duration_in_milliseconds"] = "60000",
            ["queries:by_category:cache:memory:invalidators"] = "category",
            ["queries:by_category:query"] = "SELECT {{category}} AS category, abs(random()) AS n;",

            ["queries:per_caller:route"] = "cache/per-caller",
            ["queries:per_caller:verb"] = "GET",
            ["queries:per_caller:authorize:provider"] = "idp",
            ["queries:per_caller:cache:memory:duration_in_milliseconds"] = "60000",
            ["queries:per_caller:cache:memory:invalidators"] = "category,email",
            ["queries:per_caller:query"] = "SELECT {{category}} AS category, abs(random()) AS n;",
        })
        {
            Idp = new FakeIdp(Http, "https://idp.test", "api://cache");
        }
    }

    private async Task<long> GetAsync(string path, string email)
    {
        var claims = FakeIdp.Claims();
        claims["email"] = email;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", engine.Idp.Issue(claims));
        using var response = await engine.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("n").GetInt64();
    }

    [Fact]
    public async Task EachQueryStringValue_HasItsOwnEntry_ThoughAVarSharesItsName()
    {
        var books = await GetAsync("cache/by-category?category=books", "ann@example.com");
        var games = await GetAsync("cache/by-category?category=games", "ann@example.com");

        Assert.NotEqual(books, games);
        Assert.Equal(books, await GetAsync("cache/by-category?category=books", "ann@example.com"));
    }

    [Fact]
    public async Task Callers_ShareAnEntry_WhenNoClaimOfTheirsIsAnInvalidator()
    {
        var ann = await GetAsync("cache/by-category?category=music", "ann@example.com");

        Assert.Equal(ann, await GetAsync("cache/by-category?category=music", "bob@example.com"));
    }

    [Fact]
    public async Task Callers_HaveTheirOwnEntries_WhenTheirClaimIsAnInvalidator()
    {
        var ann = await GetAsync("cache/per-caller?category=books", "ann@example.com");
        var bob = await GetAsync("cache/per-caller?category=books", "bob@example.com");

        Assert.NotEqual(ann, bob);
        Assert.Equal(ann, await GetAsync("cache/per-caller?category=books", "ann@example.com"));
    }
}
