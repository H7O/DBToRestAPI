using DBToRestAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// Minimal IWebHostEnvironment for constructing StaticFileFallbackService in tests.
/// The service always supplies its own FileProvider, so the env's file providers are never used.
/// </summary>
internal sealed class TestWebHostEnvironment : IWebHostEnvironment
{
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ApplicationName { get; set; } = "DBToRestAPI.Tests";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public string EnvironmentName { get; set; } = "Development";
}

public sealed class StaticFileFallbackServiceTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _webRoot;

    public StaticFileFallbackServiceTests()
    {
        // Layout:
        //   <base>/web/index.html  ("INDEX")
        //   <base>/web/page.html   ("PAGE")
        //   <base>/secret.txt      ("SECRET")   <-- outside the static root, used for traversal tests
        _baseDir = Path.Combine(Path.GetTempPath(), "dbtorest_static_tests_" + Guid.NewGuid().ToString("N"));
        _webRoot = Path.Combine(_baseDir, "web");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "INDEX");
        File.WriteAllText(Path.Combine(_webRoot, "page.html"), "PAGE");
        File.WriteAllText(Path.Combine(_baseDir, "secret.txt"), "SECRET");
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
    }

    private static StaticFileFallbackService BuildService(Dictionary<string, string?> data)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        return new StaticFileFallbackService(
            new TestEncryptedConfiguration(config),
            new TestWebHostEnvironment(),
            NullLoggerFactory.Instance,
            NullLogger<StaticFileFallbackService>.Instance);
    }

    private StaticFileFallbackService EnabledService(params (string key, string? value)[] extra)
    {
        var data = new Dictionary<string, string?>
        {
            ["static_files:root_path"] = _webRoot,
            ["static_files:default"] = "index.html,index.htm",
        };
        foreach (var (key, value) in extra)
            data[key] = value;
        return BuildService(data);
    }

    private static DefaultHttpContext MakeContext(string method, string path, string? accept = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (accept is not null)
            ctx.Request.Headers.Accept = accept;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    // ── Serving ──────────────────────────────────────────────

    [Fact]
    public async Task Root_ServesDefaultDocument()
    {
        var service = EnabledService();
        var ctx = MakeContext("GET", "/");

        var served = await service.TryServeAsync(ctx);

        Assert.True(served);
        Assert.Equal("INDEX", ReadBody(ctx));
    }

    [Fact]
    public async Task ExplicitFile_IsServed()
    {
        var service = EnabledService();
        var ctx = MakeContext("GET", "/page.html");

        var served = await service.TryServeAsync(ctx);

        Assert.True(served);
        Assert.Equal("PAGE", ReadBody(ctx));
    }

    [Fact]
    public async Task UnknownFile_ReturnsFalse_AndLeavesResponseUntouched()
    {
        var service = EnabledService();
        var ctx = MakeContext("GET", "/does-not-exist.html");

        var served = await service.TryServeAsync(ctx);

        Assert.False(served);
        Assert.False(ctx.Response.HasStarted);
    }

    // ── Verb restriction ─────────────────────────────────────

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task NonGetOrHead_ReturnsFalse(string method)
    {
        var service = EnabledService();
        var ctx = MakeContext(method, "/page.html"); // file exists, but verb is ineligible

        var served = await service.TryServeAsync(ctx);

        Assert.False(served);
    }

    // ── Directory traversal ──────────────────────────────────

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/../../secret.txt")]
    public async Task Traversal_AboveRoot_IsBlocked(string path)
    {
        var service = EnabledService();
        var ctx = MakeContext("GET", path);

        var served = await service.TryServeAsync(ctx);

        Assert.False(served);
        Assert.DoesNotContain("SECRET", ReadBody(ctx));
    }

    // ── Disabled states ──────────────────────────────────────

    [Fact]
    public async Task NoStaticFilesBlock_IsDisabled()
    {
        var service = BuildService(new Dictionary<string, string?>
        {
            ["queries:ep:route"] = "users",
        });

        Assert.False(service.IsEnabled);
        Assert.False(await service.TryServeAsync(MakeContext("GET", "/")));
    }

    [Fact]
    public void EnabledFalse_Disables()
    {
        var service = EnabledService(("static_files:enabled", "false"));
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void RootPath_AtBaseDirectory_IsRefused()
    {
        // Pointing root at the app base directory would expose config/appsettings/db files.
        var service = BuildService(new Dictionary<string, string?>
        {
            ["static_files:root_path"] = AppContext.BaseDirectory,
        });
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void RootPath_AsAncestorOfBaseDirectory_IsRefused()
    {
        var ancestor = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var service = BuildService(new Dictionary<string, string?>
        {
            ["static_files:root_path"] = ancestor,
        });
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void NonexistentRoot_IsDisabled()
    {
        var service = BuildService(new Dictionary<string, string?>
        {
            ["static_files:root_path"] = Path.Combine(_baseDir, "no_such_folder"),
        });
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void RootPath_InsideConfigDirectory_IsRefused()
    {
        // The conventional config/ folder (under the app base dir) holds connection strings, API keys,
        // and auth secrets — a root there must be refused even though it is a descendant of the base dir.
        var service = BuildService(new Dictionary<string, string?>
        {
            ["static_files:root_path"] = Path.Combine(AppContext.BaseDirectory, "config", "assets"),
        });
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void RootPath_InsideConfiguredKeyPath_IsRefused()
    {
        // A DPAPI key ring configured outside the conventional config/ folder must also be protected.
        var keyDir = Path.Combine(_baseDir, "mykeys");
        var service = BuildService(new Dictionary<string, string?>
        {
            ["settings_encryption:data_protection_key_path"] = keyDir,
            ["static_files:root_path"] = Path.Combine(keyDir, "nested"),
        });
        Assert.False(service.IsEnabled);
    }

    // ── SPA fallback ─────────────────────────────────────────

    [Fact]
    public async Task SpaFallback_ServesIndex_ForHtmlNavigation()
    {
        var service = EnabledService(("static_files:spa_fallback", "true"));
        var ctx = MakeContext("GET", "/app/some/client/route", accept: "text/html");

        var served = await service.TryServeAsync(ctx);

        Assert.True(served);
        Assert.Equal("INDEX", ReadBody(ctx));
    }

    [Fact]
    public async Task SpaFallback_DoesNotServeIndex_ForNonHtmlRequest()
    {
        var service = EnabledService(("static_files:spa_fallback", "true"));
        // An asset-style request (no text/html in Accept) for a missing file should 404, not get index.html.
        var ctx = MakeContext("GET", "/app/missing.js", accept: "*/*");

        var served = await service.TryServeAsync(ctx);

        Assert.False(served);
    }

    [Fact]
    public async Task SpaFallbackDisabled_MissReturnsFalse()
    {
        var service = EnabledService(); // spa_fallback defaults to false
        var ctx = MakeContext("GET", "/app/some/client/route", accept: "text/html");

        var served = await service.TryServeAsync(ctx);

        Assert.False(served);
    }
}
