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
        // Step6MandatoryFieldsCheck logs a caller who left, as one line. The copy had already started,
        // so nothing of it may stay in the store.
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        using var harness = CreateHarness(_ => Task.CompletedTask, loggerFactory.CreateLogger<Step7FileUploadManagement>());
        harness.Context.Features.Set<IHttpRequestLifetimeFeature>(new LeavesAfter(liveReads: 1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.False(File.Exists(harness.StoredFilePath));
        Assert.Empty(FilesInItsFolder(harness));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_WhenAFailedRequestOverwroteAFile_RestoresIt(bool queryThrows)
    {
        // With overwrite_existing_files on, the file already at the path is moved aside, not
        // overwritten: a failed request puts it back, and leaves nothing else in its folder.
        using var harness = CreateHarness(context =>
        {
            if (queryThrows)
                throw new InvalidOperationException("Simulated database failure");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true);
        WriteThePreviousFile(harness);

        if (queryThrows)
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Middleware.InvokeAsync(harness.Context));
        else
            await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal("previous", File.ReadAllText(harness.StoredFilePath));
        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    [Fact]
    public async Task InvokeAsync_WhenARequestThatOverwroteAFileSucceeds_KeepsOnlyTheNewFile()
    {
        using var harness = CreateHarness(_ => Task.CompletedTask, overwrite: true);
        WriteThePreviousFile(harness);

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal("probe", File.ReadAllText(harness.StoredFilePath));
        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    [Fact]
    public async Task InvokeAsync_WhenAFailedRequestWroteAPathTwice_RestoresWhatWasThereBefore()
    {
        // The second write finds the request's own first file at the path and replaces it: the
        // rollback must end with the file the path held before the request.
        using var harness = CreateHarness(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true);
        WriteThePreviousFile(harness);
        var secondFile = Path.GetTempFileName();
        File.WriteAllText(secondFile, "second");
        harness.Tracker.AddLocalFile(secondFile, "probe.txt", Path.GetRelativePath(harness.BasePath, harness.StoredFilePath));

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal("previous", File.ReadAllText(harness.StoredFilePath));
        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    [Fact]
    public async Task InvokeAsync_WhenAFailedRequestWroteANewPathTwice_LeavesNothingThere()
    {
        // The second write finds the request's own first file at the path and replaces it: the
        // rollback must not bring that file back as if it had been there before.
        using var harness = CreateHarness(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true);
        var secondFile = Path.GetTempFileName();
        File.WriteAllText(secondFile, "second");
        harness.Tracker.AddLocalFile(secondFile, "probe.txt", Path.GetRelativePath(harness.BasePath, harness.StoredFilePath));

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.False(File.Exists(harness.StoredFilePath));
        Assert.Empty(FilesInItsFolder(harness));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvokeAsync_WhenTwoStoresShareAFolder_TheRequestEndsAsIfItWroteOnce(bool previous, bool fails)
    {
        // The second store finds the first store's copy at the path, the request's own file, and
        // replaces it. Only a file that was there before the request is moved aside and put back.
        using var harness = CreateHarness(context =>
        {
            if (fails)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true, sharedFolder: true);
        if (previous)
            WriteThePreviousFile(harness);

        await harness.Middleware.InvokeAsync(harness.Context);

        if (fails && !previous)
        {
            Assert.Empty(FilesInItsFolder(harness));
            return;
        }
        Assert.Equal(fails ? "previous" : "probe", File.ReadAllText(harness.StoredFilePath));
        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    [Fact]
    public async Task InvokeAsync_WhenTheCallerLeavesWhileAReplacementIsCopied_TheFileStaysInPlace()
    {
        // The copy is cut off with a file at the path: that file is moved only once its replacement
        // is complete, so it never left.
        using var harness = CreateHarness(_ => Task.CompletedTask, overwrite: true);
        WriteThePreviousFile(harness);
        harness.Context.Features.Set<IHttpRequestLifetimeFeature>(new LeavesAfter(liveReads: 1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.Equal("previous", File.ReadAllText(harness.StoredFilePath));
        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    [Fact]
    public async Task InvokeAsync_WhenThePathIsTakenWhileTheFileIsCopied_Is409()
    {
        // Without overwrite_existing_files, the path was free when the copy started and was taken before
        // the copy got its name, by another request's upload say: a 409, as for a file there from the
        // start. A folder at the path stands in for that, as the check before the copy looks for a file.
        using var harness = CreateHarness(_ => Task.CompletedTask);
        Directory.CreateDirectory(harness.StoredFilePath);

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(StatusCodes.Status409Conflict, harness.Context.Response.StatusCode);
        Assert.True(Directory.Exists(harness.StoredFilePath));
        Assert.Empty(FilesInItsFolder(harness));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_WhenAStoreFailsToWriteAFile_KeepsTheFileItWouldHaveReplaced(bool optional)
    {
        // The copy fails part-way. The file already at the path is moved only once its replacement is
        // complete, and a rollback deletes only what the request put in place. An optional store's
        // failure doesn't fail the request, so no rollback runs there at all.
        var wentOn = false;
        using var harness = CreateHarness(_ => { wentOn = true; return Task.CompletedTask; }, overwrite: true, optional: optional,
            copy: FailPartWay);
        WriteThePreviousFile(harness);

        if (optional)
            await harness.Middleware.InvokeAsync(harness.Context);
        else
            await Assert.ThrowsAsync<IOException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.Equal(optional, wentOn);
        Assert.Equal("previous", File.ReadAllText(harness.StoredFilePath));
        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    [Fact]
    public async Task InvokeAsync_WhenTheSourceCantBeRead_KeepsTheFileItWouldHaveReplaced()
    {
        // The store fails before anything is written: a rollback deletes only what the request put in place.
        using var harness = CreateHarness(_ => Task.CompletedTask, overwrite: true);
        WriteThePreviousFile(harness);
        File.Delete(harness.Tracker.GetLocalFiles().Single().Key);

        await Assert.ThrowsAsync<FileNotFoundException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.Equal("previous", File.ReadAllText(harness.StoredFilePath));
        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    [Fact]
    public async Task InvokeAsync_WhenAFailedRequestWroteOneNameInTwoCases_LeavesNothingThere()
    {
        // probe.txt and PROBE.txt are one file where the file system ignores case (Windows, macOS), and
        // the second write must know it as the request's own; elsewhere they are two files. Either way a
        // failed request leaves nothing.
        using var harness = CreateHarness(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true);
        var secondFile = Path.GetTempFileName();
        File.WriteAllText(secondFile, "second");
        var upperCase = Path.Combine(Path.GetDirectoryName(Path.GetRelativePath(harness.BasePath, harness.StoredFilePath))!, "PROBE.txt");
        harness.Tracker.AddLocalFile(secondFile, "PROBE.txt", upperCase);

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Empty(FilesInItsFolder(harness));
    }

    [Fact]
    public async Task InvokeAsync_WhenTheFileItWouldReplaceIsBeingDownloaded_TheDownloadKeepsItsContent()
    {
        // A download opens its file with FileShare.Read. Windows can't move such a file aside, so
        // the request fails and the file stays as it was. Elsewhere the move succeeds: the request
        // replaces the file, and the download goes on reading the previous one.
        using var harness = CreateHarness(_ => Task.CompletedTask, overwrite: true);
        WriteThePreviousFile(harness);

        using (var download = new FileStream(harness.StoredFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (OperatingSystem.IsWindows())
            {
                await Assert.ThrowsAnyAsync<IOException>(() => harness.Middleware.InvokeAsync(harness.Context));
                Assert.Equal("previous", File.ReadAllText(harness.StoredFilePath));
            }
            else
            {
                await harness.Middleware.InvokeAsync(harness.Context);
                Assert.Equal("probe", File.ReadAllText(harness.StoredFilePath));
            }
            using var reader = new StreamReader(download);
            Assert.Equal("previous", reader.ReadToEnd());
        }

        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    /// <summary>Writes the first bytes, then fails, as a full disk would.</summary>
    private static async Task FailPartWay(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        var read = await source.ReadAsync(buffer, cancellationToken);
        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        await destination.FlushAsync(cancellationToken);
        throw new IOException("Simulated failure part-way through the copy");
    }

    private static void WriteThePreviousFile(TestHarness harness)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(harness.StoredFilePath)!);
        File.WriteAllText(harness.StoredFilePath, "previous");
    }

    private static string[] FilesInItsFolder(TestHarness harness) =>
        Directory.GetFiles(Path.GetDirectoryName(harness.StoredFilePath)!).Select(f => Path.GetFileName(f)).Order().ToArray();

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
        RequestDelegate next, ILogger<Step7FileUploadManagement>? logger = null, bool overwrite = false, bool optional = false,
        bool sharedFolder = false, Func<Stream, Stream, CancellationToken, Task>? copy = null)
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
                ["file_management:local_file_store:primary:optional"] = optional ? "true" : "false",
                // A second store in the same folder.
                ["file_management:local_file_store:mirror:base_path"] = sharedFolder ? basePath : null,
                ["queries:upload:file_management:stores"] = sharedFolder ? "primary,mirror" : "primary",
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
            logger ?? Mock.Of<ILogger<Step7FileUploadManagement>>())
        {
            CopyAsync = copy ?? ((source, destination, cancellationToken) => source.CopyToAsync(destination, cancellationToken))
        };

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