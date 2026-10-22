using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DBToRestAPI.Tests;

/// <summary>
/// docs/topics/14-query-chaining.md on SQL Server: the "Basic Configuration" chain, and the
/// "Cross-Database Example" with its registry query, whose SQL runs on any database, on the same
/// server. The page shows no tables, so they are built here from the columns its queries use.
/// </summary>
public class ChainingExampleTests(ChainingExampleTests.Engine engine) : IClassFixture<ChainingExampleTests.Engine>
{
    private const string Tables = """
        CREATE TABLE users (id INT PRIMARY KEY, name NVARCHAR(100), email NVARCHAR(500));
        CREATE TABLE orders (id INT PRIMARY KEY, user_email NVARCHAR(500), total DECIMAL(10, 2));
        INSERT INTO users VALUES (1, 'John', 'j@x.com'), (2, 'Mary', 'm@x.com');
        INSERT INTO orders VALUES (10, 'j@x.com', 25.00), (11, 'j@x.com', 40.00), (12, 'm@x.com', 5.00);

        CREATE TABLE permissions (email NVARCHAR(255), perm NVARCHAR(50));
        CREATE TABLE CUSTOMER_REGISTRY (ID NVARCHAR(50), FULL_NAME NVARCHAR(255), STATUS NVARCHAR(20));
        CREATE TABLE customers (id NVARCHAR(50) PRIMARY KEY, validated BIT NOT NULL DEFAULT 0, validated_by NVARCHAR(255));
        INSERT INTO permissions VALUES ('alice@example.com', 'validate');
        INSERT INTO CUSTOMER_REGISTRY VALUES ('C1', 'Acme Ltd', 'ACTIVE');
        INSERT INTO customers (id) VALUES ('C1'), ('C2');
        """;

    public sealed class Engine : SqlServerExampleEngine
    {
        public FakeIdp Idp { get; }

        public Engine() : this(new SqlServerExample(
            ("docs/topics/14-query-chaining.md", "Basic Configuration"), ("docs/topics/14-query-chaining.md", "Cross-Database Example")))
        {
        }

        // The registry query's `db2` connection is the same database here.
        private Engine(SqlServerExample example) : base(example, _ => new()
        {
            ["ConnectionStrings:db2"] = example.ConnectionString,
            ["ConnectionStrings:db2:provider"] = "Microsoft.Data.SqlClient",
            ["authorize:providers:my_provider:authority"] = "https://idp.test",
            ["authorize:providers:my_provider:audience"] = "api://docs",
        })
        {
            Idp = new FakeIdp(Http, "https://idp.test", "api://docs");
            Example.Execute(Tables);
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, string? email = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (email != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", engine.Idp.Issue(new() { ["sub"] = email, ["email"] = email }));
        using var response = await engine.Client.SendAsync(request);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
    }

    [SqlServerFact]
    public async Task TheSecondQuery_ReadsTheFirstQuerysRow()
    {
        var (status, orders) = await GetAsync("workflow?user_id=1");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([10, 11], orders.EnumerateArray().Select(o => o.GetProperty("id").GetInt32()));
    }

    [SqlServerFact]
    public async Task AFirstQueryWithNoRow_LeavesTheColumnNull()
    {
        var (status, orders) = await GetAsync("workflow?user_id=99");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, orders.GetArrayLength());
    }

    [SqlServerFact]
    public async Task ACustomerInTheRegistry_IsValidated_ByTheCaller()
    {
        var (status, body) = await GetAsync("customers/C1/validate", "alice@example.com");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Validated", body.GetProperty("status").GetString());
        Assert.Equal("C1", body.GetProperty("id").GetString());
        Assert.Equal("alice@example.com", engine.Example.Scalar("SELECT validated_by FROM customers WHERE id = 'C1' AND validated = 1"));
    }

    [SqlServerTheory]
    [InlineData("customers/C1/validate", "bob@example.com", HttpStatusCode.Forbidden, "Not authorized")]
    [InlineData("customers/C2/validate", "alice@example.com", HttpStatusCode.NotFound, "Customer not found in the registry")]
    public async Task AQueryThatThrows_StopsTheChain_WithItsStatus(string path, string email, HttpStatusCode expected, string message)
    {
        var (status, body) = await GetAsync(path, email);

        Assert.Equal(expected, status);
        Assert.Equal(message, body.GetProperty("message").GetString());
        Assert.Equal((int)expected, body.GetProperty("error_number").GetInt32());
    }
}
