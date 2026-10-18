using System.Net;
using System.Text;
using System.Text.Json;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DBToRestAPI.Tests;

/// <summary>
/// max_payload_size_in_bytes is applied to each request from the current configuration, so a change
/// takes effect without a restart.
/// </summary>
public class PayloadLimitTests
{
    private sealed class BodySize(bool readOnly = false) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => readOnly;
        public long? MaxRequestBodySize { get; set; } = 1;
    }

    private static DefaultHttpContext Request(BodySize bodySize)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(bodySize);
        return context;
    }

    private static IConfigurationRoot Settings(string? limit) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["max_payload_size_in_bytes"] = limit }).Build();

    [Fact]
    public void AChangedLimit_AppliesToTheNextRequest()
    {
        var configuration = Settings("1000");
        var first = new BodySize();
        var firstRequest = Request(first);
        PayloadLimit.Apply(firstRequest, configuration);
        Assert.Equal(1000, first.MaxRequestBodySize);
        Assert.Equal(1000, firstRequest.FormOptions.MultipartBodyLengthLimit);

        configuration["max_payload_size_in_bytes"] = "2000";
        var second = new BodySize();
        var secondRequest = Request(second);
        PayloadLimit.Apply(secondRequest, configuration);

        Assert.Equal(2000, second.MaxRequestBodySize);
        Assert.Equal(2000, secondRequest.FormOptions.MultipartBodyLengthLimit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12O")]
    [InlineData("0")]
    [InlineData("-5")]
    public void AValueThatIsntAPositiveWholeNumber_IsTheDefault(string? limit)
    {
        Assert.Equal(PayloadLimit.Default, PayloadLimit.Read(Settings(limit)));
    }

    [Theory]
    [InlineData("12O")]
    [InlineData("-5")]
    public void AValueThatIsntAPositiveWholeNumber_DoesntStopTheEngine(string limit)
    {
        // The test host passes UseSetting values as command-line arguments, so Program.cs reads this one
        // as the engine starts.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("max_payload_size_in_bytes", limit));

        Assert.Equal(PayloadLimit.Default, factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        Assert.Equal(PayloadLimit.Default, factory.Services.GetRequiredService<IOptions<FormOptions>>().Value.MultipartBodyLengthLimit);
    }

    [Fact]
    public void ABodyAlreadyBeingRead_KeepsItsLimit()
    {
        var bodySize = new BodySize(readOnly: true);

        PayloadLimit.Apply(Request(bodySize), Settings("1000"));

        Assert.Equal(1, bodySize.MaxRequestBodySize);
    }

    private sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["max_payload_size_in_bytes"] = "1000",
        ["queries:payload_length:route"] = "payload/length",
        ["queries:payload_length:verb"] = "POST",
        ["queries:payload_length:query"] = "SELECT length({{text}}) AS n;",
    }, kestrel: true);

    [Fact]
    public async Task ARaisedLimit_TakesEffectWithoutARestart()
    {
        using var engine = new Engine();
        var body = JsonSerializer.Serialize(new { text = new string('x', 2000) });
        StringContent Content() => new(body, Encoding.UTF8, "application/json");

        using (var refused = await engine.Client.PostAsync("payload/length", Content()))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);

        engine.Services.GetRequiredService<IConfiguration>()["max_payload_size_in_bytes"] = "100000";

        using var accepted = await engine.Client.PostAsync("payload/length", Content());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2000, JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement.GetProperty("n").GetInt32());
    }
}
