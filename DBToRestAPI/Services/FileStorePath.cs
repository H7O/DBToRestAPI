using System.Diagnostics.CodeAnalysis;

namespace DBToRestAPI.Services;

/// <summary>
/// Resolves the <c>relative_path</c> a download query returned against a file store's configured
/// <c>base_path</c>, and refuses any result that would land outside that store. The query may build the
/// path from caller input, so a rooted value, a <c>..</c> segment or a UNC or device path must never reach
/// the disk or the SFTP server unchecked.
/// </summary>
public static class FileStorePath
{
    /// <summary>
    /// Builds the full path of a file in a local store. Returns false when the result is not strictly
    /// under <paramref name="basePath"/>. On success, open exactly <paramref name="fullPath"/>: it is the
    /// normalised path that was checked.
    /// </summary>
    public static bool TryResolveLocal(string basePath, string relativePath, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;

        // GetFullPath below resolves '..' for ordinary paths, but leaves \\?\ paths untouched, so a base
        // path written that way would let '..' through the prefix check. No stored file needs '..'.
        if (HasParentSegment(relativePath)) return false;

        try
        {
            // The base path is used as configured, so a UNC share (\\fileserver\share\) keeps its leading
            // pair of separators. The separator goes on before GetFullPath, so "D:" means D:\ and a
            // trailing space in "D:\uploads " stays part of the folder name, exactly as the upload side
            // writes it. Path.Combine returns the relative part on its own when that part is rooted
            // (C:\..., /etc/..., \\host\share\..., \??\UNC\...), and the prefix check then refuses it
            // unless it still points inside the store.
            // '/' becomes '\' on Windows up front (a no-op elsewhere), because GetFullPath leaves a \\?\ path
            // as it is, and Windows does not read '/' as a separator in one.
            var root = WithTrailingSeparator(Path.GetFullPath(WithTrailingSeparator(basePath)));
            var relative = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, relative));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (candidate.Length <= root.Length || !candidate.StartsWith(root, comparison)) return false;

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An embedded NUL or similar: not a path to anything in the store.
            return false;
        }
    }

    /// <summary>
    /// Builds the remote path of a file in an SFTP store. SFTP paths are resolved as text: '\' becomes '/',
    /// empty and '.' segments are dropped, and a path is rooted when it starts with '/' or, for an SFTP
    /// server on Windows, with a drive letter. A drive path is always written "/C:/...": SSH.NET puts the
    /// login folder in front of any path that does not start with '/', and Windows OpenSSH reads
    /// "/C:/..." as C:\... An empty <paramref name="basePath"/> means the SFTP account's home folder, so a
    /// rooted result is refused there. Returns false when the result is not strictly under the base path.
    /// </summary>
    public static bool TryResolveSftp(string basePath, string relativePath, [NotNullWhen(true)] out string? remotePath)
    {
        remotePath = null;
        if (HasParentSegment(relativePath)) return false;

        var root = NormalizeRemote(basePath);
        var relative = NormalizeRemote(relativePath);
        var rootPrefix = root.Length == 0 || root.EndsWith('/') ? root : root + "/";

        string candidate;
        bool inside;
        if (rootPrefix.Length == 0)
        {
            candidate = relative;
            inside = candidate.Length > 0 && !IsRemoteRooted(candidate);
        }
        else
        {
            candidate = IsRemoteRooted(relative) ? relative : rootPrefix + relative;
            inside = candidate.Length > rootPrefix.Length && candidate.StartsWith(rootPrefix, StringComparison.Ordinal);
        }
        if (!inside) return false;

        remotePath = candidate;
        return true;
    }

    private static bool HasParentSegment(string path)
        => path.Split('/', '\\').Any(segment => segment == "..");

    private static string WithTrailingSeparator(string path)
        => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static bool IsRemoteRooted(string normalized) => normalized.StartsWith('/');

    /// <summary>
    /// Collapses a remote path: '\' becomes '/', empty and '.' segments go, '..' removes the segment
    /// before it. A leading '/' is kept, and a drive prefix (C:, /C:) becomes "/C:/" with the letter in
    /// upper case. Only the configured base path can still hold '..' here (the relative part was refused
    /// above if it had one): in a rooted base, '/..' is just '/', and in a base relative to the home
    /// folder (../shared) a leading '..' is kept as written.
    /// </summary>
    private static string NormalizeRemote(string path)
    {
        path = path.Replace('\\', '/');
        var prefix = "";
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            prefix = "/" + char.ToUpperInvariant(path[0]) + ":/";
            path = path[2..];
        }
        else if (path.Length >= 3 && path[0] == '/' && path[2] == ':' && char.IsAsciiLetter(path[1]))
        {
            prefix = "/" + char.ToUpperInvariant(path[1]) + ":/";
            path = path[3..];
        }
        else if (path.StartsWith('/'))
        {
            prefix = "/";
        }
        var rooted = prefix.Length > 0;
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
                else if (!rooted) segments.Add(segment);
                continue;
            }
            segments.Add(segment);
        }
        return prefix + string.Join('/', segments);
    }
}
