using DBToRestAPI.Services;
using Moq;

namespace DBToRestAPI.Tests;

/// <summary>
/// Pins how a download resolves the <c>relative_path</c> its query returned: the store's base path is used
/// exactly as written (so a UNC share keeps its leading \\), and any result outside the store is refused,
/// whether it gets there by '..', a rooted path, a UNC path to another host or a device path.
/// </summary>
public class FileStorePathTests
{
    // ── local store, Windows ─────────────────────────────────────────────────────────────────────

    [WindowsOnlyTheory]
    [InlineData(@"\\fileserver\share\uploads\", "2026/Oct/04/guid/a.pdf", @"\\fileserver\share\uploads\2026\Oct\04\guid\a.pdf")]
    [InlineData(@"\\fileserver\share\uploads", "2026/Oct/04/guid/a.pdf", @"\\fileserver\share\uploads\2026\Oct\04\guid\a.pdf")]
    [InlineData("//fileserver/share/uploads/", "2026/Oct/04/guid/a.pdf", @"\\fileserver\share\uploads\2026\Oct\04\guid\a.pdf")]
    [InlineData(@"C:\uploads\", "2026//Oct/04/a.pdf", @"C:\uploads\2026\Oct\04\a.pdf")]
    [InlineData(@"C:\\uploads\\", "a.pdf", @"C:\uploads\a.pdf")]
    [InlineData(@"C:\uploads\", "./2026/a.pdf", @"C:\uploads\2026\a.pdf")]
    [InlineData(@"C:\uploads\", @"C:\uploads\2026\a.pdf", @"C:\uploads\2026\a.pdf")]
    [InlineData(@"C:\uploads\", @"c:\UPLOADS\a.pdf", @"c:\UPLOADS\a.pdf")]
    [InlineData(@"\\?\C:\uploads\", "a.pdf", @"\\?\C:\uploads\a.pdf")]
    [InlineData(@"\\?\C:\uploads\", "2026/Oct/a.pdf", @"\\?\C:\uploads\2026\Oct\a.pdf")]
    [InlineData("D:", "2026/a.pdf", @"D:\2026\a.pdf")]
    [InlineData(@"C:\uploads ", "2026/a.pdf", @"C:\uploads \2026\a.pdf")]
    public void Local_OnWindows_PathInsideTheStoreResolves(string basePath, string relativePath, string expected)
    {
        Assert.True(FileStorePath.TryResolveLocal(basePath, relativePath, out var fullPath));
        Assert.Equal(expected, fullPath);
    }

    [WindowsOnlyTheory]
    [InlineData(@"C:\uploads\", @"..\secret.txt")]
    [InlineData(@"C:\uploads\", "../secret.txt")]
    [InlineData(@"C:\uploads\", "2026/../../secret.txt")]
    [InlineData(@"C:\uploads\", @"2026\..\a.pdf")]
    [InlineData(@"C:\uploads\", @"C:\Windows\win.ini")]
    [InlineData(@"C:\uploads\", @"\Windows\win.ini")]
    [InlineData(@"C:\uploads\", "/Windows/win.ini")]
    [InlineData(@"C:\uploads\", @"D:\a.pdf")]
    [InlineData(@"C:\uploads\", "C:a.pdf")]
    [InlineData(@"C:\uploads", @"C:\uploads-other\a.pdf")]
    [InlineData(@"C:\uploads\", @"\\otherhost\share\a.pdf")]
    [InlineData(@"C:\uploads\", "//otherhost/share/a.pdf")]
    [InlineData(@"C:\uploads\", @"\\\otherhost\share\a.pdf")]
    [InlineData(@"C:\uploads\", @"\??\UNC\otherhost\share\a.pdf")]
    [InlineData(@"C:\uploads\", "/??/UNC/otherhost/share/a.pdf")]
    [InlineData(@"C:\uploads\", @"\\?\C:\Windows\win.ini")]
    [InlineData(@"C:\uploads\", @"\\.\C:\Windows\win.ini")]
    [InlineData(@"\\fileserver\share\uploads\", @"\\fileserver\share\other\a.pdf")]
    [InlineData(@"\\?\C:\uploads\", @"..\secret.txt")]
    [InlineData(@"C:\uploads\", "")]
    [InlineData(@"C:\uploads\", ".")]
    [InlineData(@"C:\uploads\", "a.pdf\0.txt")]
    public void Local_OnWindows_PathOutsideTheStoreIsRefused(string basePath, string relativePath)
    {
        Assert.False(FileStorePath.TryResolveLocal(basePath, relativePath, out var fullPath));
        Assert.Null(fullPath);
    }

    // ── local store, Linux and macOS ─────────────────────────────────────────────────────────────

    [UnixOnlyTheory]
    [InlineData("/data/uploads/", "2026/Oct/04/guid/a.pdf", "/data/uploads/2026/Oct/04/guid/a.pdf")]
    [InlineData("/data/uploads", "2026//Oct/04/a.pdf", "/data/uploads/2026/Oct/04/a.pdf")]
    [InlineData("/data//uploads/", "a.pdf", "/data/uploads/a.pdf")]
    [InlineData("/data/uploads/", "./2026/a.pdf", "/data/uploads/2026/a.pdf")]
    [InlineData("/data/uploads/", "/data/uploads/2026/a.pdf", "/data/uploads/2026/a.pdf")]
    public void Local_OnUnix_PathInsideTheStoreResolves(string basePath, string relativePath, string expected)
    {
        Assert.True(FileStorePath.TryResolveLocal(basePath, relativePath, out var fullPath));
        Assert.Equal(expected, fullPath);
    }

    [UnixOnlyTheory]
    [InlineData("/data/uploads/", "../secret")]
    [InlineData("/data/uploads/", "2026/../../etc/passwd")]
    [InlineData("/data/uploads/", "/etc/passwd")]
    [InlineData("/data/uploads/", "//etc/passwd")]
    [InlineData("/data/uploads", "/data/uploads-other/a.pdf")]
    [InlineData("/data/uploads/", "")]
    [InlineData("/data/uploads/", "a.pdf\0.txt")]
    public void Local_OnUnix_PathOutsideTheStoreIsRefused(string basePath, string relativePath)
    {
        Assert.False(FileStorePath.TryResolveLocal(basePath, relativePath, out var fullPath));
        Assert.Null(fullPath);
    }

    // ── local store, any OS, against a real folder ───────────────────────────────────────────────

    [Fact]
    public void Local_SiblingFolderWithTheSamePrefixIsRefused()
    {
        var store = Path.Combine(Path.GetTempPath(), "dbtorest_store");
        var sibling = Path.Combine(Path.GetTempPath(), "dbtorest_store_other", "a.pdf");
        var inside = Path.Combine(store, "2026", "a.pdf");

        Assert.False(FileStorePath.TryResolveLocal(store, sibling, out _));
        Assert.True(FileStorePath.TryResolveLocal(store, inside, out var fullPath));
        Assert.Equal(Path.GetFullPath(inside), fullPath);
    }

    // ── SFTP store (POSIX paths, resolved as text, so these run on every OS) ─────────────────────

    [Theory]
    [InlineData("/site3/", "2026/Oct/a.pdf", "/site3/2026/Oct/a.pdf")]
    [InlineData("/site3", @"2026\Oct\a.pdf", "/site3/2026/Oct/a.pdf")]
    [InlineData("/site3/", "/site3/2026/a.pdf", "/site3/2026/a.pdf")]
    [InlineData("//site3//", "a.pdf", "/site3/a.pdf")]
    [InlineData("/site3/", "./2026/./a.pdf", "/site3/2026/a.pdf")]
    [InlineData("/", "a.pdf", "/a.pdf")]
    [InlineData("", "2026/a.pdf", "2026/a.pdf")]
    [InlineData("site3/", "a.pdf", "site3/a.pdf")]
    [InlineData("../shared", "a.pdf", "../shared/a.pdf")]
    // Drive paths always come out as /C:/...: SSH.NET would put the login folder in front of C:/...
    [InlineData("/C:/sftp/uploads/", "2026/a.pdf", "/C:/sftp/uploads/2026/a.pdf")]
    [InlineData("C:/sftp/uploads/", "2026/a.pdf", "/C:/sftp/uploads/2026/a.pdf")]
    [InlineData("C:/sftp/uploads/", "C:/sftp/uploads/2026/a.pdf", "/C:/sftp/uploads/2026/a.pdf")]
    [InlineData("C:/sftp/uploads/", "/C:/sftp/uploads/2026/a.pdf", "/C:/sftp/uploads/2026/a.pdf")]
    [InlineData("/c:/sftp/uploads", @"C:\sftp\uploads\2026\a.pdf", "/C:/sftp/uploads/2026/a.pdf")]
    [InlineData("C:", "a.pdf", "/C:/a.pdf")]
    public void Sftp_PathInsideTheStoreResolves(string basePath, string relativePath, string expected)
    {
        Assert.True(FileStorePath.TryResolveSftp(basePath, relativePath, out var remotePath));
        Assert.Equal(expected, remotePath);
    }

    [Theory]
    [InlineData("/site3/", "../a.pdf")]
    [InlineData("/site3/", "2026/../../a.pdf")]
    [InlineData("/site3/", @"\..\..\etc\passwd")]
    [InlineData("/site3/", "/etc/passwd")]
    [InlineData("/site3/", "//etc/passwd")]
    [InlineData("/site3", "/site34/a.pdf")]
    [InlineData("/site3/", "/site3")]
    [InlineData("/site3/", "")]
    [InlineData("", "/etc/passwd")]
    [InlineData("", "../x")]
    [InlineData("", "C:/Windows/win.ini")]
    [InlineData("", @"C:\Windows\win.ini")]
    [InlineData("/site3/", "c:/a.pdf")]
    [InlineData("site3", "/site3/a.pdf")]
    [InlineData("C:/sftp/uploads/", "C:/Windows/win.ini")]
    [InlineData("C:/sftp/uploads/", "D:/sftp/uploads/a.pdf")]
    [InlineData("C:/sftp/uploads/", "/etc/passwd")]
    public void Sftp_PathOutsideTheStoreIsRefused(string basePath, string relativePath)
    {
        Assert.False(FileStorePath.TryResolveSftp(basePath, relativePath, out var remotePath));
        Assert.Null(remotePath);
    }

    // ── upload side: the paths it builds must stay inside the store too ──────────────────────────

    [Theory]
    [InlineData(@"\tmp\pwned.pdf")]
    [InlineData(@"..\x.pdf")]
    [InlineData("a/b.pdf")]
    [InlineData(@"a\b.pdf")]
    public void Upload_FileNameWithASeparatorIsRejectedOnEveryOs(string fileName)
    {
        Assert.Throws<ArgumentException>(() => ParametersBuilder.ValidateAndGetNormalizeFileName(fileName));
    }

    [Theory]
    [InlineData("/{file{name}}", "a.pdf", "a.pdf")]
    [InlineData("//{file{name}}", "a.pdf", "a.pdf")]
    [InlineData(@"\{file{name}}", "a.pdf", "a.pdf")]
    [InlineData("files/{file{name}}", "a.pdf", "files/a.pdf")]
    public void Upload_StructureWithALeadingSeparatorStaysRelative(string structure, string fileName, string expected)
    {
        var builder = new ParametersBuilder(
            Mock.Of<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            Mock.Of<IEncryptedConfiguration>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ParametersBuilder>.Instance);
        Assert.Equal(expected, builder.BuildRelativeFilePath(structure, fileName));
    }
}

/// <summary>A theory that runs only on Windows and is reported as skipped elsewhere.</summary>
internal sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = @"Windows path rules (UNC shares, '\' separator).";
    }
}

/// <summary>A theory that runs only on Linux and macOS and is reported as skipped on Windows.</summary>
internal sealed class UnixOnlyTheoryAttribute : TheoryAttribute
{
    public UnixOnlyTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Linux and macOS path rules ('/' separator).";
    }
}
