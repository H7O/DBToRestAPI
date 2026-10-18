using DBToRestAPI.Middlewares;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

    [Fact]
    public async Task InvokeAsync_WhenTheCallerLeavesWhileFilesAreStored_RollsBackWithoutAWarningOrAnError()
    {
        // Step6MandatoryFieldsCheck logs a caller who left, as one line. The copy had already created
        // the file in the store, so the rollback must delete it.
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        using var harness = CreateHarness(_ => Task.CompletedTask, loggerFactory.CreateLogger<Step7FileUploadManagement>());
        harness.Context.Features.Set<IHttpRequestLifetimeFeature>(new LeavesAfter(liveReads: 1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.False(File.Exists(harness.StoredFilePath));
        Assert.Contains(logs.Entries, e => e.Message.Contains("Rolling back uploaded files"));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task InvokeAsync_WhenTheCallerLeavesOnceFilesAreStored_RollsBackInsteadOfGoingOn()
    {
        // A store whose write doesn't stop with the caller (an SFTP upload) finishes; the caller is
        // gone by then. The request must not go on to the query: Step6MandatoryFieldsCheck logs it.
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var wentOn = false;
        using var harness = CreateHarness(_ => { wentOn = true; return Task.CompletedTask; },
            loggerFactory.CreateLogger<Step7FileUploadManagement>());
        // Live for the entry check and the copy, gone from then on.
        harness.Context.Features.Set<IHttpRequestLifetimeFeature>(new LeavesAfter(liveReads: 2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.False(wentOn);
        Assert.False(File.Exists(harness.StoredFilePath));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task InvokeAsync_WhenTheFileItWouldReplaceCantBeOpened_KeepsThatFile()
    {
        // With overwrite_existing_files on, a copy that fails before it opens its file must not
        // delete the file already there. A download holding it open is one way that happens.
        using var harness = CreateHarness(_ => Task.CompletedTask, overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(harness.StoredFilePath)!);
        File.WriteAllText(harness.StoredFilePath, "previous");

        using (new FileStream(harness.StoredFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            await Assert.ThrowsAnyAsync<IOException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.Equal("previous", File.ReadAllText(harness.StoredFilePath));
    }

    /// <summary>The caller is still there for the first reads of RequestAborted, and gone from then on.</summary>
    private sealed class LeavesAfter : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource _left = new();
        private readonly int _liveReads;
        private int _reads;

        public LeavesAfter(int liveReads)
        {
            _liveReads = liveReads;
            _left.Cancel();
        }

        public CancellationToken RequestAborted
        {
            get => Interlocked.Increment(ref _reads) <= _liveReads ? CancellationToken.None : _left.Token;
            set { }
        }

        public void Abort() { }
    }

    private static TestHarness CreateHarness(
        RequestDelegate next, ILogger<Step7FileUploadManagement>? logger = null, bool overwrite = false)
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
                ["queries:upload:file_management:stores"] = "primary",
                ["queries:upload:file_management:overwrite_existing_files"] = overwrite ? "true" : "false"
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
            logger ?? Mock.Of<ILogger<Step7FileUploadManagement>>());

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