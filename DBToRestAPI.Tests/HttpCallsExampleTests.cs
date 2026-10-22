using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DBToRestAPI.Tests;

/// <summary>
/// docs/topics/17-embedded-http-calls.md, "Complete example: POST with a form body", on SQL Server:
/// the pay_for_order chain and its tables, with the payment API answered in process, and each row of
/// its "What the client gets back" table.
/// </summary>
public class HttpCallsExampleTests(HttpCallsExampleTests.Engine engine) : IClassFixture<HttpCallsExampleTests.Engine>
{
    private const string PaymentIntents = "https://api.stripe.com/v1/payment_intents";

    public sealed class Engine : SqlServerExampleEngine
    {
        public FakeIdp Idp { get; }

        public Engine() : base(new SqlServerExample(("docs/topics/17-embedded-http-calls.md", "Complete example")), _ => new()
        {
            ["vars:stripe_secret_key"] = "sk_test_docs",
            ["authorize:providers:my_provider:authority"] = "https://idp.test",
            ["authorize:providers:my_provider:audience"] = "api://docs",
        })
        {
            Idp = new FakeIdp(Http, "https://idp.test", "api://docs");
        }
    }

    /// <summary>An order of 1500 cents by <paramref name="owner"/>'s customer `cus_{owner}`.</summary>
    private Guid NewOrder(string owner, string status = "unpaid")
    {
        var customer = Guid.NewGuid();
        var order = Guid.NewGuid();
        engine.Example.Execute($"""
            INSERT INTO customers VALUES ('{customer}', '{owner}', 'cus_{owner}');
            INSERT INTO orders (id, customer_id, amount_cents, status) VALUES ('{order}', '{customer}', 1500, '{status}');
            """);
        return order;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PayAsync(Guid order, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"orders/{order}/payment");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", engine.Idp.Issue(new() { ["sub"] = user }));
        using var response = await engine.Client.SendAsync(request);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
    }

    private int CallsFor(string customer) => engine.Http.Requests.Count(r => r.Uri.ToString() == PaymentIntents && r.Body?.Contains($"customer={customer}") == true);

    [SqlServerFact]
    public async Task TheOwner_Pays_WithTheFormTheDatabaseFilled()
    {
        engine.Http.Map(PaymentIntents, _ => FakeHttpServer.Json(new { id = "pi_ann", client_secret = "cs_ann" }));
        var order = NewOrder("ann");

        var (status, body) = await PayAsync(order, "ann");

        Assert.True(status == HttpStatusCode.OK, body.ToString());
        Assert.Equal("pi_ann", body.GetProperty("payment_intent_id").GetString());
        Assert.Equal("cs_ann", body.GetProperty("client_secret").GetString());
        var call = Assert.Single(engine.Http.Requests, r => r.Uri.ToString() == PaymentIntents && r.Body?.Contains("customer=cus_ann") == true);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("amount=1500&currency=usd&customer=cus_ann", call.Body);
        Assert.StartsWith("application/x-www-form-urlencoded", call.Headers["Content-Type"]);
        Assert.Equal("Bearer sk_test_docs", call.Authorization);
        Assert.Equal("pending_payment", engine.Example.Scalar($"SELECT status FROM orders WHERE id = '{order}' AND payment_intent_id = 'pi_ann'"));
    }

    [SqlServerFact]
    public async Task AnotherUsersOrder_Is404_AndNothingIsCalled()
    {
        var order = NewOrder("bea");

        var (status, body) = await PayAsync(order, "someone-else");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("Order not found", body.GetProperty("message").GetString());
        Assert.Equal(0, CallsFor("cus_bea"));
    }

    [SqlServerFact]
    public async Task AnOrderThatIsNotUnpaid_Is409()
    {
        var order = NewOrder("cal", status: "paid");

        var (status, body) = await PayAsync(order, "cal");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("Only an unpaid order can be paid", body.GetProperty("message").GetString());
        Assert.Equal(0, CallsFor("cus_cal"));
    }

    [SqlServerTheory]
    [InlineData("an error status", HttpStatusCode.BadGateway, "Payment service error")]
    [InlineData("no answer", HttpStatusCode.ServiceUnavailable, "Payment service unreachable")]
    public async Task APaymentServiceFailure_HasItsOwnStatus(string failure, HttpStatusCode expected, string message)
    {
        engine.Http.Map(PaymentIntents, _ => failure == "no answer"
            ? throw new HttpRequestException("Connection refused")
            : FakeHttpServer.Json(new { error = new { message = "boom" } }, HttpStatusCode.InternalServerError));
        var order = NewOrder("dan" + (int)expected);

        var (status, body) = await PayAsync(order, "dan" + (int)expected);

        Assert.Equal(expected, status);
        Assert.Equal(message, body.GetProperty("message").GetString());
        Assert.Equal("unpaid", engine.Example.Scalar($"SELECT status FROM orders WHERE id = '{order}'"));
    }

    [SqlServerFact]
    public async Task NoToken_Is401()
    {
        var order = NewOrder("fay");

        using var response = await engine.Client.PostAsync($"orders/{order}/payment", null);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Authorization header is required", body.GetProperty("message").GetString());
    }

    [SqlServerFact]
    public async Task TheSecondOfTwoPayments_Is409()
    {
        // The other request stores its PaymentIntent while this one's call is under way.
        var order = NewOrder("eve");
        engine.Http.Map(PaymentIntents, _ =>
        {
            engine.Example.Execute($"UPDATE orders SET status = 'pending_payment', payment_intent_id = 'pi_first' WHERE id = '{order}'");
            return FakeHttpServer.Json(new { id = "pi_second", client_secret = "cs_second" });
        });

        var (status, body) = await PayAsync(order, "eve");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("This order is already being paid", body.GetProperty("message").GetString());
        Assert.Equal("pi_first", engine.Example.Scalar($"SELECT payment_intent_id FROM orders WHERE id = '{order}'"));
    }
}
