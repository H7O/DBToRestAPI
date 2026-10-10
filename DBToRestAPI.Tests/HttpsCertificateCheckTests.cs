using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// At start-up the engine drops its HTTPS endpoint, with a warning, when the configured certificate
/// file is missing. It looks for a relative path where Kestrel loads it from: under the content root,
/// the working directory. Looking beside the executable instead dropped HTTPS for `dotnet run` with
/// the certificate in the project's config/certs folder.
/// </summary>
public class HttpsCertificateCheckTests : IDisposable
{
    private const string Skipped = "HTTPS endpoint skipped";
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "dbtorest-https-" + Guid.NewGuid().ToString("N"));

    public HttpsCertificateCheckTests() => Directory.CreateDirectory(_contentRoot);

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
    }

    private IReadOnlyCollection<CapturedLogs.Entry> Start()
    {
        var logs = new CapturedLogs();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_contentRoot);
            builder.UseSetting("Kestrel:Endpoints:Https:Certificate:Path", "certs/test.pfx");
            // Never every interface, should a Kestrel server ever start here.
            builder.UseSetting("Kestrel:Endpoints:Http:Url", "http://127.0.0.1:0");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        _ = factory.Services;   // starts the engine
        return logs.Entries;
    }

    [Fact]
    public void ACertificateInTheWorkingDirectory_KeepsHttps()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "certs"));
        File.WriteAllText(Path.Combine(_contentRoot, "certs", "test.pfx"), "not read here");

        Assert.DoesNotContain(Start(), e => e.Message.Contains(Skipped));
    }

    [Fact]
    public void NoCertificateInTheWorkingDirectory_SkipsHttps_WithAWarning()
    {
        Assert.Contains(Start(), e => e.Level == LogLevel.Warning && e.Message.Contains(Skipped)
                                      && e.Message.Contains(Path.Combine(_contentRoot, "certs/test.pfx")));
    }
}
