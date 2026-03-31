using DBToRestAPI.Middlewares;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace DBToRestAPI.Tests;

public class Step7FileUploadManagementTests
{
    [Fact]
    public async Task InvokeAsync_WhenDownstreamReturnsFailureStatus_RollsBackUploadedFile()
    {
        using var harness = CreateHarness(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.False(File.Exists(harness.StoredFilePath));
    }

    [Fact]
    public async Task InvokeAsync_WhenDownstreamThrows_RollsBackUploadedFile()
    {
        using var harness = CreateHarness(_ => throw new InvalidOperationException("Simulated database failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.False(File.Exists(harness.StoredFilePath));
    }

    [Fact]
    public async Task InvokeAsync_WhenDownstreamSucceeds_LeavesUploadedFileInPlace()
    {
        using var harness = CreateHarness(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.True(File.Exists(harness.StoredFilePath));
    }

    private static TestHarness CreateHarness(RequestDelegate next)
    {
        var basePath = Path.Combine(Path.GetTempPath(), "DBToRestAPI-Step7Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(basePath);

        var tempFilePath = Path.GetTempFileName();
        File.WriteAllText(tempFilePath, "probe");

        var relativePath = Path.Combine("2026", "Mar", "31", Guid.NewGuid().ToString("D"), "probe.txt");

        var tracker = new TempFilesTracker();
        tracker.AddLocalFile(tempFilePath, "probe.txt", relativePath);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["file_management:local_file_store:primary:base_path"] = basePath,
                ["queries:upload:file_management:stores"] = "primary"
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton(tracker)
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Response.Body = new MemoryStream();
        context.Items["section"] = configuration.GetSection("queries:upload");

        var middleware = new Step7FileUploadManagement(
            next,
            null!,
            new TestEncryptedConfiguration(configuration),
            Mock.Of<ILogger<Step7FileUploadManagement>>());

        return new TestHarness(
            context,
            tracker,
            services,
            middleware,
            basePath,
            Path.Combine(basePath, relativePath));
    }

    private sealed class TestHarness(
        DefaultHttpContext context,
        TempFilesTracker tracker,
        ServiceProvider services,
        Step7FileUploadManagement middleware,
        string basePath,
        string storedFilePath) : IDisposable
    {
        public DefaultHttpContext Context { get; } = context;
        public TempFilesTracker Tracker { get; } = tracker;
        public ServiceProvider Services { get; } = services;
        public Step7FileUploadManagement Middleware { get; } = middleware;
        public string BasePath { get; } = basePath;
        public string StoredFilePath { get; } = storedFilePath;

        public void Dispose()
        {
            Tracker.Dispose();
            Services.Dispose();

            if (Directory.Exists(BasePath))
            {
                Directory.Delete(BasePath, recursive: true);
            }
        }
    }
}