using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DBToRestAPI.Tests;

/// <summary>
/// docs/topics/14-query-chaining.md, "Complete example: an order with live stock from another
/// database", on SQL Server: the chain and its tables, with the warehouse connection on the same
/// database, and each row of its "What the client gets back" table that a request can produce.
/// </summary>
public class ChainingExampleTests(ChainingExampleTests.Engine engine) : IClassFixture<ChainingExampleTests.Engine>
{
    private static readonly Guid Order = Guid.Parse("6f1c2a4e-0d1b-4a52-9a49-3c3a8f0e2b11");
    private static readonly Guid EmptyOrder = Guid.Parse("0b9e3c55-6a0f-4d2b-8e57-2f8d1c7a9e40");

    public sealed class Engine : SqlServerExampleEngine
    {
        public FakeIdp Idp { get; }

        public Engine() : this(new SqlServerExample(("docs/topics/14-query-chaining.md", "Complete example")))
        {
        }

        // The page's warehouse connection is the same database here.
        private Engine(SqlServerExample example) : base(example, _ => new()
        {
            ["ConnectionStrings:warehouse"] = example.ConnectionString,
            ["ConnectionStrings:warehouse:provider"] = "Microsoft.Data.SqlClient",
            ["authorize:providers:my_provider:authority"] = "https://idp.test",
            ["authorize:providers:my_provider:audience"] = "api://docs",
        })
        {
            Idp = new FakeIdp(Http, "https://idp.test", "api://docs");
            var customer = Guid.NewGuid();
            Example.Execute($"""
                INSERT INTO customers VALUES ('{customer}', 'ann', 'Ann');
                INSERT INTO orders (id, customer_id, status) VALUES ('{Order}', '{customer}', 'placed'), ('{EmptyOrder}', '{customer}', 'placed');
                INSERT INTO order_lines VALUES ('{Order}', 'B2', 5), ('{Order}', 'A1', 2);
                INSERT INTO stock VALUES ('A1', 10), ('B2', 3);
                """);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, string? user = "ann")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (user != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", engine.Idp.Issue(new() { ["sub"] = user }));
        using var response = await engine.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [SqlServerTheory]
    [InlineData("")]
    [InlineData("?order_status=hacked")]   // a request value doesn't replace query 1's column
    public async Task TheOwner_GetsEachLineWithItsStock(string queryString)
    {
        var (status, body) = await GetAsync($"orders/{Order}/stock{queryString}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """[{"product_id":"A1","quantity":2,"available":10,"in_stock":true,"order_status":"placed"},{"product_id":"B2","quantity":5,"available":3,"in_stock":false,"order_status":"placed"}]""",
            body);
    }

    [SqlServerFact]
    public async Task AnOrderWithoutLines_IsAnEmptyArray()
    {
        var (status, body) = await GetAsync($"orders/{EmptyOrder}/stock");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("[]", body);
    }

    [SqlServerTheory]
    [InlineData("another user")]
    [InlineData("an unknown id")]
    [InlineData("an id that isn't a GUID")]
    public async Task AnyoneElse_Gets404(string who)
    {
        var (path, user) = who switch
        {
            "another user" => ($"orders/{Order}/stock", "bob"),
            "an unknown id" => ($"orders/{Guid.NewGuid()}/stock", "ann"),
            _ => ("orders/not-a-guid/stock", "ann"),
        };

        var (status, body) = await GetAsync(path, user);
        var answer = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("Order not found", answer.GetProperty("message").GetString());
        Assert.Equal(404, answer.GetProperty("error_number").GetInt32());
    }

    [SqlServerFact]
    public async Task NoToken_Is401()
    {
        var (status, body) = await GetAsync($"orders/{Order}/stock", user: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("Authorization header is required", JsonDocument.Parse(body).RootElement.GetProperty("message").GetString());
    }
}
