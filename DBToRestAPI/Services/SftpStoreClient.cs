using Com.H.Net.Ssh;
using Renci.SshNet.Common;

namespace DBToRestAPI.Services;

/// <summary>
/// The calls an upload makes to an SFTP store. A test swaps in a server that keeps its files in memory.
/// A path that isn't there is a <see cref="FileNotFoundException"/>, whatever the server.
/// </summary>
internal interface ISftpStoreClient : IDisposable
{
    /// <summary>Connects, unless connected already. Every other call connects when it needs to.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Whether anything is at <paramref name="path"/>: a file or a folder.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);

    /// <summary>Whether a file, not a folder, is at <paramref name="path"/>.</summary>
    Task<bool> IsFileAsync(string path);

    /// <summary>Creates the folders above <paramref name="path"/> that don't exist yet.</summary>
    Task UploadAsync(Stream source, string path, CancellationToken cancellationToken);

    /// <summary>On most servers it fails when <paramref name="newPath"/> is taken, as SFTP's rename does.</summary>
    Task RenameAsync(string oldPath, string newPath);

    Task DeleteAsync(string path);
}

/// <summary>
/// An SFTP store through Com.H.Net.Ssh. Its calls share one connection, which connects on the first call
/// and closes when this is disposed. After a call fails, the next one connects afresh: a call cut off
/// part-way, by the caller leaving say, leaves nothing open on the server.
/// </summary>
internal sealed class SftpStoreClient(string host, int port, string username, string password) : ISftpStoreClient
{
    private readonly SFtpClient _client = new(host, port, username, password) { KeepConnectionOpen = true };

    public Task ConnectAsync(CancellationToken cancellationToken)
        => CallAsync(string.Empty, async () =>
        {
            if (!_client.Client.IsConnected)
                await _client.Client.ConnectAsync(cancellationToken);
        });

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
        => CallAsync(path, () => _client.ExistsAsync(path, cancellationToken));

    public async Task<bool> IsFileAsync(string path)
    {
        try
        {
            return !(await CallAsync(path, () => _client.GetFileInfoAsync(path))).IsDirectory;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public Task UploadAsync(Stream source, string path, CancellationToken cancellationToken)
        => CallAsync(path, () => _client.UploadAsync(source, path, cancellationToken: cancellationToken));

    public Task RenameAsync(string oldPath, string newPath)
        => CallAsync(oldPath, () => _client.RenameAsync(oldPath, newPath));

    public Task DeleteAsync(string path)
        => CallAsync(path, () => _client.DeleteAsync(path));

    public void Dispose() => _client.Dispose();

    private Task CallAsync(string path, Func<Task> call)
        => CallAsync(path, async () => { await call(); return true; });

    private async Task<T> CallAsync<T>(string path, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (SftpPathNotFoundException ex)
        {
            // The server answered: the connection is fine.
            throw new FileNotFoundException(ex.Message, path, ex);
        }
        catch
        {
            try
            {
                _client.Disconnect();
            }
            catch
            {
                // Closed already.
            }
            throw;
        }
    }
}
