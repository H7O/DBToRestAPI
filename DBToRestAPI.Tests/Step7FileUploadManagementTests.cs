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
        // The caller leaves just as the last file is put in place, so its write finishes. The request
        // must not go on to the query: Step6MandatoryFieldsCheck logs it.
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

    // SFTP stores, against a server in memory whose rename, like most servers', doesn't replace a file.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_AFailedRequest_LeavesNothingInTheStore(bool queryThrows)
    {
        var sftp = new MemorySftp();
        using var harness = CreateHarness(context =>
        {
            if (queryThrows)
                throw new InvalidOperationException("Simulated database failure");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, sftp: sftp);

        if (queryThrows)
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Middleware.InvokeAsync(harness.Context));
        else
            await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Empty(sftp.Files);
    }

    [Fact]
    public async Task Sftp_ASuccessfulRequest_LeavesOnlyTheFileUnderItsName()
    {
        // Uploaded under a hidden name in the destination's folder, then renamed.
        var sftp = new MemorySftp();
        using var harness = CreateHarness(_ => Task.CompletedTask, sftp: sftp);
        string? uploadedTo = null;
        sftp.OnUpload = path => uploadedTo = path;

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = "probe" }, sftp.Files);
        var folder = harness.StoredFilePath[..(harness.StoredFilePath.LastIndexOf('/') + 1)];
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(folder) + @"\.[0-9a-f]{32}\.uploading$", uploadedTo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_AFailedRequestThatOverwroteAFile_PutsItBack(bool queryThrows)
    {
        var sftp = new MemorySftp();
        using var harness = CreateHarness(context =>
        {
            if (queryThrows)
                throw new InvalidOperationException("Simulated database failure");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true, sftp: sftp);
        sftp.Files[harness.StoredFilePath] = "previous";

        if (queryThrows)
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Middleware.InvokeAsync(harness.Context));
        else
            await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = "previous" }, sftp.Files);
    }

    [Fact]
    public async Task Sftp_ASuccessfulRequestThatOverwroteAFile_KeepsOnlyTheNewFile()
    {
        var sftp = new MemorySftp();
        using var harness = CreateHarness(_ => Task.CompletedTask, overwrite: true, sftp: sftp);
        sftp.Files[harness.StoredFilePath] = "previous";

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = "probe" }, sftp.Files);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Sftp_AnUploadThatFailsPartWay_LeavesTheStoreAsItWas(bool overwrite, bool optional)
    {
        // The partial file has a hidden name, and only a complete upload takes the destination's name. An
        // optional store's failure doesn't fail the request, so no rollback runs there at all.
        var sftp = new MemorySftp { FailUploadPartWay = true };
        var wentOn = false;
        using var harness = CreateHarness(_ => { wentOn = true; return Task.CompletedTask; },
            overwrite: overwrite, optional: optional, sftp: sftp);
        if (overwrite)
            sftp.Files[harness.StoredFilePath] = "previous";
        var before = new Dictionary<string, string>(sftp.Files);

        if (optional)
            await harness.Middleware.InvokeAsync(harness.Context);
        else
            await Assert.ThrowsAsync<IOException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.Equal(optional, wentOn);
        Assert.Equal(before, sftp.Files);
    }

    [Fact]
    public async Task Sftp_WhenThePathIsTakenDuringTheUpload_Is409()
    {
        // Without overwrite_existing_files, the path was free when the upload started and was taken before
        // the upload got its name, by another request's say: a 409, as for a file there from the start.
        var sftp = new MemorySftp();
        using var harness = CreateHarness(_ => Task.CompletedTask, sftp: sftp);
        sftp.OnUpload = _ => sftp.Files[harness.StoredFilePath] = "another request's";

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(StatusCodes.Status409Conflict, harness.Context.Response.StatusCode);
        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = "another request's" }, sftp.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_ARequestThatWroteAPathTwice_EndsWithTheLastFileOrWhatWasThereBefore(bool fails)
    {
        // The second upload sets the request's own first file aside, like any file at the path. A failed
        // request undoes both writes, newest first, and ends with the file the path held before.
        var sftp = new MemorySftp();
        using var harness = CreateHarness(context =>
        {
            if (fails)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true, sftp: sftp);
        sftp.Files[harness.StoredFilePath] = "previous";
        var secondFile = Path.GetTempFileName();
        File.WriteAllText(secondFile, "second");
        harness.Tracker.AddLocalFile(secondFile, "probe.txt", harness.RelativePath);

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = fails ? "previous" : "second" }, sftp.Files);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Sftp_WhenTwoStoresShareAFolder_TheRequestEndsAsIfItWroteOnce(bool previous, bool fails)
    {
        // Each store sets the file at the path aside, the first store's included. A failed request undoes both
        // writes, newest first, and nothing is logged as a failure.
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var sftp = new MemorySftp();
        using var harness = CreateHarness(context =>
        {
            if (fails)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, loggerFactory.CreateLogger<Step7FileUploadManagement>(), overwrite: true, sharedFolder: true, sftp: sftp);
        if (previous)
            sftp.Files[harness.StoredFilePath] = "previous";

        await harness.Middleware.InvokeAsync(harness.Context);

        if (fails && !previous)
            Assert.Empty(sftp.Files);
        else
            Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = fails ? "previous" : "probe" }, sftp.Files);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("Failed to"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_WhenTheCallerLeavesDuringTheUpload_TheStoreStaysAsItWas(bool overwrite)
    {
        // The upload stops with the caller, part-way: nothing of it stays, and a file at the path never left.
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var sftp = new MemorySftp();
        using var harness = CreateHarness(_ => Task.CompletedTask, loggerFactory.CreateLogger<Step7FileUploadManagement>(),
            overwrite: overwrite, sftp: sftp);
        if (overwrite)
            sftp.Files[harness.StoredFilePath] = "previous";
        var before = new Dictionary<string, string>(sftp.Files);
        var uploadStarted = false;
        sftp.OnUpload = _ => uploadStarted = true;
        // Live for the entry check, the connect and, without overwrite, the check for a file at the path.
        harness.Context.Features.Set<IHttpRequestLifetimeFeature>(new LeavesAfter(liveReads: overwrite ? 2 : 3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.True(uploadStarted);
        Assert.Equal(0, sftp.CompletedUploads);
        Assert.Equal(before, sftp.Files);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_WhenTheServerRefusesARename_KeepsTheFileItWouldHaveReplaced(bool refusesSettingAside)
    {
        // A server that refuses the rename that sets the file aside, or the one that moves the upload in:
        // the store fails, and the file at the path stays as it was.
        var sftp = new MemorySftp();
        using var harness = CreateHarness(_ => Task.CompletedTask, overwrite: true, sftp: sftp);
        sftp.Files[harness.StoredFilePath] = "previous";
        sftp.OnRename = (from, _) => (refusesSettingAside ? from == harness.StoredFilePath : from.EndsWith(".uploading"))
            ? new IOException("Permission denied")
            : null;

        await Assert.ThrowsAsync<IOException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = "previous" }, sftp.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_WhenTwoAccountsReachOneFolder_TheRequestEndsAsIfItWroteOnce(bool fails)
    {
        // The second account sets the first store's file aside, as an SFTP store does with any file at the
        // path. A failed request undoes the writes newest first, each one deleted and what it replaced put
        // back, so the file that was there ends at the path. A successful one keeps the last file.
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var sftp = new MemorySftp();
        using var harness = CreateHarness(context =>
        {
            if (fails)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, loggerFactory.CreateLogger<Step7FileUploadManagement>(), overwrite: true, sharedFolder: true, sftp: sftp,
            mirrorUsername: "another account");
        sftp.Files[harness.StoredFilePath] = "previous";

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = fails ? "previous" : "probe" }, sftp.Files);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("Failed to"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_WhenTheReplyToTheMoveInIsLost_TheUploadCountsAsInPlace(bool fails)
    {
        // The server renamed the upload into place, but the connection dropped before its reply came. The
        // file at the path is the request's own: it is no 409, and a failed request deletes it.
        var sftp = new MemorySftp { LoseRenameReply = (from, _) => from.EndsWith(".uploading") };
        using var harness = CreateHarness(context =>
        {
            if (fails)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, sftp: sftp);

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(fails ? StatusCodes.Status500InternalServerError : StatusCodes.Status200OK, harness.Context.Response.StatusCode);
        if (fails)
            Assert.Empty(sftp.Files);
        else
            Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = "probe" }, sftp.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_WhenTheReplyToTheSetAsideIsLost_TheFileCountsAsSetAside(bool fails)
    {
        // The server renamed the file that was there aside, but the reply was lost: the upload goes on, and a
        // failed request puts the file back.
        var sftp = new MemorySftp();
        using var harness = CreateHarness(context =>
        {
            if (fails)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true, sftp: sftp);
        sftp.Files[harness.StoredFilePath] = "previous";
        sftp.LoseRenameReply = (from, _) => from == harness.StoredFilePath;

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = fails ? "previous" : "probe" }, sftp.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenALocalAndAnSftpStoreReachOneFolder_TheRequestEndsAsIfItWroteOnce(bool fails)
    {
        // An SFTP server on this machine serves the local store's folder. The SFTP store sets the local
        // store's file aside, as it does with any file at the path. A failed request undoes the writes
        // newest first, each one deleted and what it replaced put back.
        using var harness = CreateHarness(context =>
        {
            if (fails)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, overwrite: true, localAndSftpShareAFolder: true);
        WriteThePreviousFile(harness);

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.Equal(fails ? "previous" : "probe", File.ReadAllText(harness.StoredFilePath));
        Assert.Equal(["probe.txt"], FilesInItsFolder(harness));
    }

    [Theory]
    [InlineData("the put-back")]
    [InlineData("the rollback's delete")]
    [InlineData("the delete of the replaced file")]
    [InlineData("the delete of an unfinished upload")]
    public async Task Sftp_WhenTheReplyToAPutBackOrADeleteIsLost_NothingIsReportedAsFailed(string lost)
    {
        // The server did it, but the connection dropped before its reply came: the store ends as it should,
        // and no warning names a failure that didn't happen or a hidden file that isn't there.
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var sftp = new MemorySftp();
        var fails = lost == "the rollback's delete";
        using var harness = CreateHarness(context =>
        {
            if (fails)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, loggerFactory.CreateLogger<Step7FileUploadManagement>(), overwrite: true, optional: true, sftp: sftp);
        sftp.Files[harness.StoredFilePath] = "previous";
        switch (lost)
        {
            case "the put-back":
                sftp.OnRename = (from, _) => from.EndsWith(".uploading") ? new IOException("Permission denied") : null;
                sftp.LoseRenameReply = (from, _) => from.EndsWith(".replaced");
                break;
            case "the rollback's delete":
                sftp.LoseDeleteReply = path => path == harness.StoredFilePath;
                break;
            case "the delete of the replaced file":
                sftp.LoseDeleteReply = path => path.EndsWith(".replaced");
                break;
            default:
                sftp.FailUploadPartWay = true;
                sftp.LoseDeleteReply = path => path.EndsWith(".uploading");
                break;
        }

        await harness.Middleware.InvokeAsync(harness.Context);

        var probeStays = lost == "the delete of the replaced file";
        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = probeStays ? "probe" : "previous" }, sftp.Files);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("Failed to delete") || e.Message.Contains("Failed to restore"));
    }

    [Fact]
    public async Task Sftp_WhenAnOptionalStoreFailsToWriteAPathTheRequestWroteBefore_TheFirstFileStays()
    {
        // Two optional stores on one server and folder. The second sets the first's file aside like any
        // other, and puts it back at once when its own file can't be moved in: the request goes on with the
        // first store's file in place.
        var sftp = new MemorySftp();
        var moveIns = 0;
        sftp.OnRename = (from, _) => from.EndsWith(".uploading") && ++moveIns == 2 ? new IOException("Connection lost") : null;
        var wentOn = false;
        using var harness = CreateHarness(_ => { wentOn = true; return Task.CompletedTask; },
            overwrite: true, optional: true, sharedFolder: true, sftp: sftp);
        sftp.Files[harness.StoredFilePath] = "previous";

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.True(wentOn);
        Assert.Equal(new Dictionary<string, string> { [harness.StoredFilePath] = "probe" }, sftp.Files);
    }

    [Fact]
    public async Task Sftp_WhenAFolderIsAtThePath_TheStoreFailsAndTheFolderStays()
    {
        // With overwrite on, only a file is set aside. A folder stays, so the upload can't be moved in, as
        // in a local store.
        var sftp = new MemorySftp();
        using var harness = CreateHarness(_ => Task.CompletedTask, overwrite: true, sftp: sftp);
        sftp.Folders.Add(harness.StoredFilePath);

        await Assert.ThrowsAsync<IOException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.Equal([harness.StoredFilePath], sftp.Folders);
        Assert.Empty(sftp.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sftp_WhenTheServerCantBeReached_ItIsTriedOnce(bool overwrite)
    {
        // An optional store whose server is down: one attempt, and no warning about an upload that never
        // started.
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var sftp = new MemorySftp { Unreachable = true };
        var wentOn = false;
        using var harness = CreateHarness(_ => { wentOn = true; return Task.CompletedTask; }, loggerFactory.CreateLogger<Step7FileUploadManagement>(),
            overwrite: overwrite, optional: true, sftp: sftp);

        await harness.Middleware.InvokeAsync(harness.Context);

        Assert.True(wentOn);
        Assert.Equal(1, sftp.FailedConnects);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("unfinished upload"));
    }

    [Fact]
    public async Task Sftp_WhenTheServerRefusesTheMoveInWithoutOverwrite_ItIsAnErrorNotA409()
    {
        // An account that may write but not rename: nothing is at the path, so the server's refusal is the
        // store's error, not a file that already exists.
        var sftp = new MemorySftp { OnRename = (from, _) => from.EndsWith(".uploading") ? new IOException("Permission denied") : null };
        using var harness = CreateHarness(_ => Task.CompletedTask, sftp: sftp);

        await Assert.ThrowsAsync<IOException>(() => harness.Middleware.InvokeAsync(harness.Context));

        Assert.NotEqual(StatusCodes.Status409Conflict, harness.Context.Response.StatusCode);
        Assert.Empty(sftp.Files);
    }

    /// <summary>An SFTP server on this machine whose folder /site is a local folder.</summary>
    private sealed class DiskSftp(string folder) : ISftpStoreClient
    {
        private string OnDisk(string path) => Path.Combine(folder, path["/site/".Length..].Replace('/', Path.DirectorySeparatorChar));

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(File.Exists(OnDisk(path)) || Directory.Exists(OnDisk(path)));

        public Task<bool> IsFileAsync(string path) => Task.FromResult(File.Exists(OnDisk(path)));

        public async Task UploadAsync(Stream source, string path, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OnDisk(path))!);
            using var file = File.Create(OnDisk(path));
            await source.CopyToAsync(file, cancellationToken);
        }

        // Like SFTP's rename, File.Move doesn't replace a file.
        public Task RenameAsync(string oldPath, string newPath)
        {
            File.Move(OnDisk(oldPath), OnDisk(newPath));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path)
        {
            if (!File.Exists(OnDisk(path)))
                throw new FileNotFoundException("No such file", path);
            File.Delete(OnDisk(path));
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// An SFTP server that keeps its files in memory, by path. Its rename, like most servers', doesn't
    /// replace a file. The hooks make a call fail, or change the files as a call starts.
    /// </summary>
    private sealed class MemorySftp
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>Folders by path. Their contents aren't kept: a rename moves the folder, a delete refuses it.</summary>
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);

        /// <summary>Every call fails as against a server that can't be reached, each one a failed attempt to connect.</summary>
        public bool Unreachable { get; set; }

        public int FailedConnects { get; private set; }

        /// <summary>Called with the path as an upload starts.</summary>
        public Action<string>? OnUpload { get; set; }

        /// <summary>An upload writes its first bytes, then fails, as a lost connection would.</summary>
        public bool FailUploadPartWay { get; set; }

        /// <summary>Called with the old and new path of a rename; an exception it returns is thrown instead.</summary>
        public Func<string, string, Exception?>? OnRename { get; set; }

        /// <summary>Called with the old and new path of a rename once it is done; true loses its reply, as a dropped connection would.</summary>
        public Func<string, string, bool>? LoseRenameReply { get; set; }

        /// <summary>Called with the path of a delete once it is done; true loses its reply, as a dropped connection would.</summary>
        public Func<string, bool>? LoseDeleteReply { get; set; }

        /// <summary>Uploads written to their end.</summary>
        public int CompletedUploads { get; private set; }

        public ISftpStoreClient Connect() => new Client(this);

        private sealed class Client(MemorySftp server) : ISftpStoreClient
        {
            private void Reach()
            {
                if (!server.Unreachable)
                    return;
                server.FailedConnects++;
                throw new IOException("Connection refused");
            }

            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                Reach();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
            {
                Reach();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(server.Files.ContainsKey(path) || server.Folders.Contains(path));
            }

            public Task<bool> IsFileAsync(string path)
            {
                Reach();
                return Task.FromResult(server.Files.ContainsKey(path));
            }

            public async Task UploadAsync(Stream source, string path, CancellationToken cancellationToken)
            {
                Reach();
                server.OnUpload?.Invoke(path);
                var content = await new StreamReader(source).ReadToEndAsync(CancellationToken.None);
                server.Files[path] = content[..2];
                if (server.FailUploadPartWay)
                    throw new IOException("Simulated failure part-way through the upload");
                cancellationToken.ThrowIfCancellationRequested();
                server.Files[path] = content;
                server.CompletedUploads++;
            }

            public Task RenameAsync(string oldPath, string newPath)
            {
                Reach();
                if (server.OnRename?.Invoke(oldPath, newPath) is { } refused)
                    throw refused;
                if (server.Files.ContainsKey(newPath) || server.Folders.Contains(newPath))
                    throw new IOException("File already exists");
                if (server.Folders.Remove(oldPath))
                {
                    server.Folders.Add(newPath);
                    return Task.CompletedTask;
                }
                if (!server.Files.TryGetValue(oldPath, out var content))
                    throw new FileNotFoundException("No such file", oldPath);
                server.Files.Remove(oldPath);
                server.Files[newPath] = content;
                if (server.LoseRenameReply?.Invoke(oldPath, newPath) == true)
                    throw new IOException("Connection was closed by the server.");
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string path)
            {
                Reach();
                if (server.Folders.Contains(path))
                    throw new IOException("Failure");
                if (!server.Files.Remove(path))
                    throw new FileNotFoundException("No such file", path);
                if (server.LoseDeleteReply?.Invoke(path) == true)
                    throw new IOException("Connection was closed by the server.");
                return Task.CompletedTask;
            }

            public void Dispose() { }
        }
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
        bool sharedFolder = false, Func<Stream, Stream, CancellationToken, Task>? copy = null, MemorySftp? sftp = null,
        string? mirrorUsername = null, bool localAndSftpShareAFolder = false)
    {
        var basePath = Path.Combine(Path.GetTempPath(), "DBToRestAPI-Step7Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(basePath);

        var tempFilePath = Path.GetTempFileName();
        File.WriteAllText(tempFilePath, "probe");

        var relativePath = Path.Combine("2026", "Mar", "31", Guid.NewGuid().ToString("D"), "probe.txt");

        var tracker = new TempFilesTracker();
        tracker.AddLocalFile(tempFilePath, "probe.txt", relativePath);

        var settings = new Dictionary<string, string?>
        {
            ["queries:upload:file_management:stores"] = sharedFolder || localAndSftpShareAFolder ? "primary,mirror" : "primary",
            ["queries:upload:file_management:overwrite_existing_files"] = overwrite ? "true" : "false"
        };
        if (sftp is null)
        {
            settings["file_management:local_file_store:primary:base_path"] = basePath;
            settings["file_management:local_file_store:primary:optional"] = optional ? "true" : "false";
            // A second store in the same folder.
            if (sharedFolder)
                settings["file_management:local_file_store:mirror:base_path"] = basePath;
            if (localAndSftpShareAFolder)
            {
                // Or an SFTP store whose server serves that folder.
                settings["file_management:sftp_file_store:mirror:host"] = "localhost";
                settings["file_management:sftp_file_store:mirror:username"] = "t";
                settings["file_management:sftp_file_store:mirror:password"] = "p";
                settings["file_management:sftp_file_store:mirror:base_path"] = "/site";
            }
        }
        else
        {
            // The second store is on the same server, in the same folder, through the same account unless told otherwise.
            foreach (var store in sharedFolder ? new[] { "primary", "mirror" } : ["primary"])
            {
                settings[$"file_management:sftp_file_store:{store}:host"] = "sftp.test";
                settings[$"file_management:sftp_file_store:{store}:username"] = store == "mirror" ? mirrorUsername ?? "t" : "t";
                settings[$"file_management:sftp_file_store:{store}:password"] = "p";
                settings[$"file_management:sftp_file_store:{store}:base_path"] = "/site";
                settings[$"file_management:sftp_file_store:{store}:optional"] = optional ? "true" : "false";
            }
        }
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
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
            CopyAsync = copy ?? ((source, destination, cancellationToken) => source.CopyToAsync(destination, cancellationToken)),
            ConnectSftp = _ => localAndSftpShareAFolder
                ? new DiskSftp(basePath)
                : (sftp ?? throw new InvalidOperationException("No SFTP server in this test")).Connect()
        };

        return new TestHarness(
            context,
            tracker,
            services,
            middleware,
            basePath,
            relativePath,
            sftp is null ? Path.Combine(basePath, relativePath) : "/site/" + relativePath.Replace('\\', '/'));
    }

    private sealed class TestHarness(
        DefaultHttpContext context,
        TempFilesTracker tracker,
        ServiceProvider services,
        Step7FileUploadManagement middleware,
        string basePath,
        string relativePath,
        string storedFilePath) : IDisposable
    {
        public DefaultHttpContext Context { get; } = context;
        public TempFilesTracker Tracker { get; } = tracker;
        public ServiceProvider Services { get; } = services;
        public Step7FileUploadManagement Middleware { get; } = middleware;
        public string BasePath { get; } = basePath;
        public string RelativePath { get; } = relativePath;
        /// <summary>The upload's path in the store: on disk, or on the SFTP server.</summary>
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