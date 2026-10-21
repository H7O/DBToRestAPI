using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Com.H.IO;
using SftpServer = (string Host, int Port, string Username, string Password);

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// Custom exception for file already exists errors during upload.
    /// Used to distinguish from other errors and return HTTP 409 Conflict.
    /// </summary>
    public class FileAlreadyExistsException : Exception
    {
        public FileAlreadyExistsException(string message) : base(message) { }
    }

    public class StoreOperationTracker
    {
        public IConfigurationSection Config { get; }
        public bool? WasSuccessful { get; set; }
        public StoreOperationTracker(IConfigurationSection config)
        {
            Config = config;
            WasSuccessful = null;
        }
    }
    public class Step7FileUploadManagement(
        RequestDelegate next,
        SettingsService settings,
        IEncryptedConfiguration settingsEncryptionService,
        ILogger<Step7FileUploadManagement> logger
        )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        // private readonly IConfiguration _configuration = configuration;
        private readonly IEncryptedConfiguration _configuration = settingsEncryptionService;
        private readonly ILogger<Step7FileUploadManagement> _logger = logger;
        private static readonly string _errorCode = "Step 7 - File Upload Management Error";

        /// <summary>Copies an upload into a local store. A test swaps it for one that fails part-way.</summary>
        internal Func<Stream, Stream, CancellationToken, Task> CopyAsync { get; init; } =
            (source, destination, cancellationToken) => source.CopyToAsync(destination, cancellationToken);

        /// <summary>Connects to an SFTP store's server. A test swaps in one that keeps its files in memory.</summary>
        internal Func<SftpServer, ISftpStoreClient> ConnectSftp { get; init; } =
            server => new SftpStoreClient(server.Host, server.Port, server.Username, server.Password);

        /// <summary>A file named <paramref name="name"/> in the folder of the remote <paramref name="path"/>.</summary>
        private static string BesideRemote(string path, string name)
        {
            var folderEnd = path.LastIndexOf('/');
            return folderEnd < 0 ? name : path[..(folderEnd + 1)] + name;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            #region log the time and the middleware name
            if (this._logger.IsEnabled(LogLevel.Debug))
                this._logger.LogDebug("{time}: in Step6FileManagement middleware",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
            #endregion


            var tempFilesTracker = context.RequestServices.GetRequiredService<TempFilesTracker>();
            if (!tempFilesTracker.GetLocalFiles().Any())
            {
                // Proceed to the next middleware
                await this._next(context);
                return;
            }

            #region if no section passed from the previous middlewares, return 500
            IConfigurationSection? section = context.Items.ContainsKey("section")
                ? context.Items["section"] as IConfigurationSection
                : null;


            if (section == null)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );

                return;
            }

            #endregion

            #region check if the request was cancelled
            if (context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Request was cancelled"
                        }
                    )
                    {
                        StatusCode = 400
                    }
                );

                return;
            }
            #endregion

            #region get settings for file management
            // Get file management settings from `file_management` section
            var fileManagementSection = section.GetSection("file_management");
            var defaultFileStoresSettings = this._configuration.GetSection("file_management");
            if (!fileManagementSection.Exists()
                && !defaultFileStoresSettings.Exists())
            {
                // No file management settings found, proceed to next middleware
                await this._next(context);
                return;
            }

            #endregion

            var stores = fileManagementSection.GetValue<string>("stores");
            if (string.IsNullOrWhiteSpace(stores))
            {
                stores = defaultFileStoresSettings.GetValue<string>("stores");
            }

            if (string.IsNullOrWhiteSpace(stores))
            {
                // No stores defined, proceed to next middleware
                await this._next(context);
                return;
            }

            // Get overwrite_existing_files setting (endpoint-specific -> global -> default false)
            // If false, uploading a file that already exists in the store will throw an exception
            // If true, existing files will be silently overwritten
            var overwriteExistingFiles = fileManagementSection.GetValue<bool?>("overwrite_existing_files")
                ?? defaultFileStoresSettings.GetValue<bool?>("overwrite_existing_files")
                ?? false;

            var storesList = stores.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();

            if (!storesList.Any())
            {
                // No valid stores found, proceed to next middleware
                await this._next(context);
                return;
            }

            var localStores = new List<StoreOperationTracker>();
            var sftpStores = new List<StoreOperationTracker>();
            foreach (var store in storesList)
            {
                var storeSection = defaultFileStoresSettings.GetSection($"local_file_store:{store}");
                if (storeSection.Exists())
                {
                    localStores.Add(new(storeSection));
                    continue;
                }
                storeSection = defaultFileStoresSettings.GetSection($"sftp_file_store:{store}");
                if (storeSection.Exists())
                {
                    sftpStores.Add(new(storeSection));
                    continue;
                }

                _logger.LogWarning("{time}: Store '{store}' not found in configuration for Step6FileManagement middleware",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                    store);
            }

            // group SFTP stores by connection settings (host, port, username, password) to minimize connections
            var groupedSftpStores = sftpStores
                // filter out stores with missing connection settings
                .Where(s =>
                {
                    return !string.IsNullOrWhiteSpace(s.Config.GetValue<string>("host"))
                        && !string.IsNullOrWhiteSpace(s.Config.GetValue<string>("username"))
                        && !string.IsNullOrWhiteSpace(s.Config.GetValue<string>("password"));
                })
                .GroupBy(s => (SftpServer)(
                    s.Config.GetValue<string>("host")!,
                    s.Config.GetValue<int>("port", 22),
                    s.Config.GetValue<string>("username")!,
                    s.Config.GetValue<string>("password")!))
                .ToList();

            if (!localStores.Any() && !groupedSftpStores.Any())
            {
                // No valid stores found, proceed to next middleware
                await this._next(context);
                return;
            }

            // Every file this request put in place, in order, with the file it replaced there when
            // overwrite_existing_files is on, moved aside in its folder until the request ends. A failed request
            // undoes them newest first, deleting each file and putting back the one it replaced, so a store ends
            // as it was, even where two stores reach one folder by different paths (a link, a share and its local
            // path, another SFTP account). A successful one deletes the replaced files. Server is null for a
            // local store.
            var placed = new List<(SftpServer? Server, string Path, string? AsidePath, string StoreName)>();

            // The files this request put in place in local stores, by full path (ignoring case where the file
            // system does), so a later write to the same path knows the file there is its own: the same path
            // twice in one request, or two stores sharing a folder.
            var placedPaths = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

            // One client per SFTP server, for a rollback or for deleting the replaced files.
            ISftpStoreClient ClientFor(Dictionary<SftpServer, ISftpStoreClient> clients, SftpServer server)
            {
                if (!clients.TryGetValue(server, out var sftpClient))
                    clients[server] = sftpClient = ConnectSftp(server);
                return sftpClient;
            }

            async Task RollbackUploadedFilesAsync(string rollbackReason)
            {
                // Expected when the caller left (a 499 included), so not a warning then.
                this._logger.Log(context.RequestAborted.IsCancellationRequested ? LogLevel.Debug : LogLevel.Warning,
                    "{time}: Rolling back uploaded files in Step6FileManagement middleware due to {rollbackReason}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                    rollbackReason);

                // Rollback removes files written by this request, but intentionally
                // does not prune parent directories. Folder trees may be shared by
                // concurrent uploads, and GUID segments can be caller-defined or omitted.
                var sftpClients = new Dictionary<SftpServer, ISftpStoreClient>();
                try
                {
                    for (var i = placed.Count - 1; i >= 0; i--)
                    {
                        var (server, path, asidePath, storeName) = placed[i];
                        if (server is { } sftpServer)
                        {
                            var sftpClient = ClientFor(sftpClients, sftpServer);
                            try
                            {
                                await sftpClient.DeleteAsync(path);
                                this._logger.LogDebug("{time}: Deleted file '{file}' from SFTP store ({sftp_store_name}) during rollback in Step6FileManagement middleware",
                                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                    LogText.Escape(path),
                                    storeName);
                            }
                            catch (FileNotFoundException)
                            {
                                // Gone already: nothing to delete.
                            }
                            catch (Exception ex2)
                            {
                                // Deleted all the same when only the reply was lost.
                                if (!await WasDeletedAsync(sftpClient, path))
                                {
                                    this._logger.LogWarning("{time}: Failed to delete file '{file}' from SFTP store ({sftp_store_name}) during rollback in Step6FileManagement middleware"
                                        + Environment.NewLine
                                        + " error: {errorMessage}"
                                        ,
                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                        LogText.Escape(path),
                                        storeName,
                                        ex2.Message
                                        );
                                }
                            }
                            if (asidePath is not null)
                                await PutBackRemoteAsync(sftpClient, path, asidePath, storeName);
                        }
                        else
                        {
                            try
                            {
                                if (File.Exists(path))
                                {
                                    File.Delete(path);
                                    this._logger.LogDebug("{time}: Deleted file '{file}' from local store ({local_store_name}) during rollback in Step6FileManagement middleware",
                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                        LogText.Escape(path),
                                        storeName);
                                }
                            }
                            catch (Exception ex2)
                            {
                                this._logger.LogWarning("{time}: Failed to delete file '{file}' from local store ({local_store_name}) during rollback in Step6FileManagement middleware"
                                    + Environment.NewLine
                                    + " error: {errorMessage}"
                                    ,
                                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                    LogText.Escape(path),
                                    storeName,
                                    ex2.Message
                                    );
                            }
                            if (asidePath is not null)
                                PutBack(path, asidePath, storeName);
                        }
                    }
                    placed.Clear();
                }
                finally
                {
                    foreach (var sftpClient in sftpClients.Values)
                        sftpClient.Dispose();
                }

                this._logger.LogDebug("{time}: files rolled back in Step6FileManagement middleware",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
            }

            // The request succeeded, so the files its uploads replaced are no longer needed.
            async Task DeleteSetAsideFilesAsync()
            {
                var sftpClients = new Dictionary<SftpServer, ISftpStoreClient>();
                try
                {
                    foreach (var (server, path, asidePath, storeName) in placed)
                    {
                        if (asidePath is null)
                            continue;
                        try
                        {
                            if (server is { } sftpServer)
                                await ClientFor(sftpClients, sftpServer).DeleteAsync(asidePath);
                            else
                                File.Delete(asidePath);
                        }
                        catch (Exception ex2)
                        {
                            // Deleted all the same when only the reply was lost.
                            if (server is { } lostReplyServer && await WasDeletedAsync(ClientFor(sftpClients, lostReplyServer), asidePath))
                                continue;
                            this._logger.LogWarning("{time}: Failed to delete '{asideFile}', the previous content of '{file}' in store ({store_name}), in Step6FileManagement middleware"
                                + Environment.NewLine
                                + " error: {errorMessage}"
                                ,
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                LogText.Escape(asidePath),
                                LogText.Escape(path),
                                storeName,
                                ex2.Message
                                );
                        }
                    }
                    placed.Clear();
                }
                finally
                {
                    foreach (var sftpClient in sftpClients.Values)
                        sftpClient.Dispose();
                }
            }

            // Moves a file that was set aside back to its path, replacing whatever this request put there
            // unless told not to. One that can't be moved back stays where it is, named in the log, rather
            // than being lost.
            void PutBack(string path, string asidePath, string storeName, bool replace = true)
            {
                try
                {
                    File.Move(asidePath, path, overwrite: replace);
                    this._logger.LogDebug("{time}: Restored file '{file}' in local store ({local_store_name}) in Step6FileManagement middleware",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                        LogText.Escape(path),
                        storeName);
                }
                catch (Exception ex2)
                {
                    this._logger.LogWarning("{time}: Failed to restore file '{file}' in local store ({local_store_name}) in Step6FileManagement middleware;"
                        + " its previous content is in '{asideFile}'"
                        + Environment.NewLine
                        + " error: {errorMessage}"
                        ,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                        LogText.Escape(path),
                        storeName,
                        LogText.Escape(asidePath),
                        ex2.Message
                        );
                }
            }

            // The same for an SFTP store, whose rename never replaces a file: the path must be free.
            async Task PutBackRemoteAsync(ISftpStoreClient sftpClient, string path, string asidePath, string storeName)
            {
                try
                {
                    await sftpClient.RenameAsync(asidePath, path);
                    this._logger.LogDebug("{time}: Restored file '{file}' in SFTP store ({sftp_store_name}) in Step6FileManagement middleware",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                        LogText.Escape(path),
                        storeName);
                }
                catch (Exception ex2)
                {
                    // Put back all the same when only the reply was lost.
                    if (await WasRenamedAsync(sftpClient, asidePath, path) == true)
                        return;
                    this._logger.LogWarning("{time}: Failed to restore file '{file}' in SFTP store ({sftp_store_name}) in Step6FileManagement middleware;"
                        + " its previous content is in '{asideFile}'"
                        + Environment.NewLine
                        + " error: {errorMessage}"
                        ,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                        LogText.Escape(path),
                        storeName,
                        LogText.Escape(asidePath),
                        ex2.Message
                        );
                }
            }

            // Deletes what an SFTP upload left under its hidden name, when it never got its destination's name.
            async Task DeleteUnfinishedUploadAsync(ISftpStoreClient sftpClient, string writingPath, string storeName)
            {
                try
                {
                    await sftpClient.DeleteAsync(writingPath);
                }
                catch (FileNotFoundException)
                {
                    // The upload failed before it created the file.
                }
                catch (Exception ex2)
                {
                    // Deleted all the same when only the reply was lost.
                    if (await WasDeletedAsync(sftpClient, writingPath))
                        return;
                    this._logger.LogWarning("{time}: Failed to delete the unfinished upload '{file}' in SFTP store ({sftp_store_name}) in Step6FileManagement middleware"
                        + Environment.NewLine
                        + " error: {errorMessage}"
                        ,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                        LogText.Escape(writingPath),
                        storeName,
                        ex2.Message
                        );
                }
            }

            // Whether a rename that failed was carried out all the same, its reply lost when the connection dropped
            // say: the old path gone and the new one there. Null when the server can't say.
            static async Task<bool?> WasRenamedAsync(ISftpStoreClient sftpClient, string oldPath, string newPath)
            {
                try
                {
                    return !await sftpClient.ExistsAsync(oldPath, CancellationToken.None)
                        && await sftpClient.ExistsAsync(newPath, CancellationToken.None);
                }
                catch
                {
                    return null;
                }
            }

            // Whether a delete that failed was carried out all the same, its reply lost: nothing at the path now.
            // False when the server can't say.
            static async Task<bool> WasDeletedAsync(ISftpStoreClient sftpClient, string path)
            {
                try
                {
                    return !await sftpClient.ExistsAsync(path, CancellationToken.None);
                }
                catch
                {
                    return false;
                }
            }

            // Whether something is at a remote path, to tell why a rename failed; false when the server can't say.
            static async Task<bool> IsTakenAsync(ISftpStoreClient sftpClient, string path)
            {
                try
                {
                    return await sftpClient.ExistsAsync(path, CancellationToken.None);
                }
                catch
                {
                    return false;
                }
            }

            try
            {



                foreach (var entry in localStores)
                {
                    var localPath = entry.Config.GetValue<string>("base_path");
                    if (string.IsNullOrWhiteSpace(localPath))
                        continue;

                    try
                    {
                        foreach (var file in tempFilesTracker.GetLocalFiles())
                        {
                            // Same containment rule as downloads: nothing is written outside the store.
                            if (!FileStorePath.TryResolveLocal(localPath, file.Value.RelativePath, out var destinationPath))
                                throw new InvalidOperationException(
                                    $"File path '{file.Value.RelativePath}' points outside local store '{entry.Config.Key}'.");
                            // see if the parent directory exists, if not create it
                            var parentDir = Path.GetDirectoryName(destinationPath);
                            if (!string.IsNullOrWhiteSpace(parentDir))
                            {
                                Directory.CreateDirectory(parentDir!);
                            }

                            // Check if file already exists (unless overwrite is enabled)
                            if (!overwriteExistingFiles && File.Exists(destinationPath))
                            {
                                throw new FileAlreadyExistsException(
                                    $"File '{file.Value.RelativePath}' already exists in store '{entry.Config.Key}'. " +
                                    "Set 'overwrite_existing_files' to true to allow overwriting.");
                            }

                            // The copy is written under a hidden name beside its destination and takes the
                            // destination's name only once complete: a write that fails part-way leaves nothing
                            // at the path, and a file already there stays readable meanwhile.
                            var writingPath = Path.Combine(parentDir!, $".{Guid.NewGuid():N}.uploading");
                            try
                            {
                                using (var sourceStream = new FileStream(file.Key, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true))
                                using (var destStream = new FileStream(writingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
                                {
                                    await CopyAsync(sourceStream, destStream, context.RequestAborted);
                                }

                                // With overwrite on, the file already there is moved aside rather than replaced,
                                // so a rollback can put it back. One this request put there itself is simply
                                // replaced: it isn't a file to put back.
                                string? asidePath = null;
                                if (overwriteExistingFiles && File.Exists(destinationPath) && !placedPaths.Contains(destinationPath))
                                {
                                    asidePath = Path.Combine(parentDir!, $".{Guid.NewGuid():N}.replaced");
                                    try
                                    {
                                        File.Move(destinationPath, asidePath);
                                    }
                                    catch (FileNotFoundException)
                                    {
                                        // Moved away since, by another request replacing it too: nothing to set aside.
                                        asidePath = null;
                                    }
                                }

                                try
                                {
                                    File.Move(writingPath, destinationPath, overwrite: overwriteExistingFiles);
                                }
                                catch (IOException) when (!overwriteExistingFiles && Path.Exists(destinationPath))
                                {
                                    // Something took the path while the file was copied, another request's upload
                                    // say: the same answer as for a file that was there from the start.
                                    throw new FileAlreadyExistsException(
                                        $"File '{file.Value.RelativePath}' already exists in store '{entry.Config.Key}'. " +
                                        "Set 'overwrite_existing_files' to true to allow overwriting.");
                                }
                                catch when (asidePath is not null)
                                {
                                    // At once, as an optional store's request goes on without this file, and without
                                    // replacing a file another request put there since.
                                    PutBack(destinationPath, asidePath, entry.Config.Key, replace: false);
                                    throw;
                                }
                                placedPaths.Add(destinationPath);

                                // Recorded once in place, so a rollback deletes only files this request put there.
                                placed.Add((null, destinationPath, asidePath, entry.Config.Key));
                            }
                            finally
                            {
                                // What a copy that failed left, or one whose destination couldn't take it.
                                try
                                {
                                    File.Delete(writingPath);
                                }
                                catch (Exception ex2)
                                {
                                    this._logger.LogWarning("{time}: Failed to delete the unfinished copy '{file}' in local store ({local_store_name}) in Step6FileManagement middleware"
                                        + Environment.NewLine
                                        + " error: {errorMessage}"
                                        ,
                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                        LogText.Escape(writingPath),
                                        entry.Config.Key,
                                        ex2.Message
                                        );
                                }
                            }

                            this._logger.LogDebug("{time}: Copied temp file '{tempFile}' to local store ({local_store_name})  at '{destinationPath}' in Step6FileManagement middleware",
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                LogText.Escape(file.Key),
                                entry.Config.Key,
                                LogText.Escape(destinationPath));
                        }

                        entry.WasSuccessful = true;
                    }
                    catch (Exception ex)
                    {
                        entry.WasSuccessful = false;
                        // The caller left: nothing failed, and Step6MandatoryFieldsCheck logs it as one line.
                        if (context.RequestAborted.IsCancellationRequested)
                            throw;
                        bool optional = entry.Config.GetValue<bool>("optional", false);
                        if (optional)
                        {
                            this._logger.LogWarning("{time}: Failed to copy files to optional local store ({local_store_name})"
                                + " in Step6FileManagement middleware, continuing"
                                + Environment.NewLine
                                + " error: {errorMessage}"
                                ,
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                entry.Config.Key,
                                ex.Message
                                );
                            continue;
                        }
                        this._logger.LogError("{time}: Failed to copy files to local store ({local_store_name}) in Step6FileManagement middleware"
                            + Environment.NewLine
                            + " error: {errorMessage}"
                            ,
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                            entry.Config.Key,
                            ex.Message
                            );
                        throw; // Re-throw exception to be caught by outer catch
                    }
                }


                foreach (var group in groupedSftpStores)
                {
                    using var sftpClient = ConnectSftp(group.Key);
                    foreach (var entry in group)
                    {
                        // remote path can be empty, in which case files are uploaded to the user's home directory
                        var remotePath = entry.Config.GetValue<string>("base_path", string.Empty);

                        try
                        {
                            // Before any file, so a server that can't be reached, or refuses the login, fails the
                            // store at once, with nothing to clean up.
                            await sftpClient.ConnectAsync(context.RequestAborted);

                            foreach (var file in tempFilesTracker.GetLocalFiles())
                            {
                                // Same containment rule as downloads: nothing is written outside the store.
                                if (!FileStorePath.TryResolveSftp(remotePath, file.Value.RelativePath, out var destinationPath))
                                    throw new InvalidOperationException(
                                        $"File path '{file.Value.RelativePath}' points outside SFTP store '{entry.Config.Key}'.");

                                // Check if file already exists on SFTP (unless overwrite is enabled)
                                if (!overwriteExistingFiles && await sftpClient.ExistsAsync(destinationPath, context.RequestAborted))
                                {
                                    throw new FileAlreadyExistsException(
                                        $"File '{file.Value.RelativePath}' already exists in SFTP store '{entry.Config.Key}'. " +
                                        "Set 'overwrite_existing_files' to true to allow overwriting.");
                                }

                                // As in a local store, the upload is written under a hidden name beside its
                                // destination and takes the destination's name only once complete.
                                var writingPath = BesideRemote(destinationPath, $".{Guid.NewGuid():N}.uploading");
                                var movedIn = false;
                                try
                                {
                                    using (var fileStream = new FileStream(file.Key, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true))
                                    {
                                        await sftpClient.UploadAsync(fileStream, writingPath, context.RequestAborted);
                                    }

                                    // SFTP's rename doesn't replace a file, so with overwrite on a file at the path is
                                    // moved aside first, one this request put there included: a rollback puts it back,
                                    // a success deletes it. A folder there stays, and the move-in then fails, as in a
                                    // local store. A path another request takes in between fails the store, which loses
                                    // no file.
                                    string? asidePath = null;
                                    if (overwriteExistingFiles && await sftpClient.IsFileAsync(destinationPath))
                                    {
                                        asidePath = BesideRemote(destinationPath, $".{Guid.NewGuid():N}.replaced");
                                        try
                                        {
                                            await sftpClient.RenameAsync(destinationPath, asidePath);
                                        }
                                        catch (FileNotFoundException)
                                        {
                                            // Moved away since, by another request replacing it too: nothing to set aside.
                                            asidePath = null;
                                        }
                                        catch (Exception ex2)
                                        {
                                            // Set aside all the same when only the reply was lost.
                                            var renamed = await WasRenamedAsync(sftpClient, destinationPath, asidePath);
                                            if (renamed != true)
                                            {
                                                if (renamed is null)
                                                    this._logger.LogWarning("{time}: Failed to set aside file '{file}' in SFTP store ({sftp_store_name}) in Step6FileManagement middleware;"
                                                        + " its previous content may be in '{asideFile}'"
                                                        + Environment.NewLine
                                                        + " error: {errorMessage}"
                                                        ,
                                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                                        LogText.Escape(destinationPath),
                                                        entry.Config.Key,
                                                        LogText.Escape(asidePath),
                                                        ex2.Message
                                                        );
                                                throw;
                                            }
                                        }
                                    }

                                    try
                                    {
                                        await sftpClient.RenameAsync(writingPath, destinationPath);
                                    }
                                    catch (Exception)
                                    {
                                        // In place all the same when only the reply was lost.
                                        if (await WasRenamedAsync(sftpClient, writingPath, destinationPath) != true)
                                        {
                                            if (asidePath is not null)
                                            {
                                                // At once, as an optional store's request goes on without this file.
                                                await PutBackRemoteAsync(sftpClient, destinationPath, asidePath, entry.Config.Key);
                                            }
                                            else if (!overwriteExistingFiles && await IsTakenAsync(sftpClient, destinationPath))
                                            {
                                                // Something took the path during the upload, another request's say: the
                                                // same answer as for a file that was there from the start.
                                                throw new FileAlreadyExistsException(
                                                    $"File '{file.Value.RelativePath}' already exists in SFTP store '{entry.Config.Key}'. " +
                                                    "Set 'overwrite_existing_files' to true to allow overwriting.");
                                            }
                                            throw;
                                        }
                                    }
                                    movedIn = true;

                                    // Recorded once in place, so a rollback deletes only files this request put there.
                                    placed.Add((group.Key, destinationPath, asidePath, entry.Config.Key));
                                }
                                finally
                                {
                                    // What an upload that failed left, or one whose destination couldn't take it.
                                    if (!movedIn)
                                        await DeleteUnfinishedUploadAsync(sftpClient, writingPath, entry.Config.Key);
                                }

                                this._logger.LogDebug("{time}: Uploaded temp file '{tempFile}' to SFTP store ({sftp_store_name}) at '{destinationPath}' in Step6FileManagement middleware",
                                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                    LogText.Escape(file.Key),
                                    entry.Config.Key,
                                    LogText.Escape(destinationPath));
                            }

                            entry.WasSuccessful = true;
                        }
                        catch (Exception ex)
                        {
                            entry.WasSuccessful = false;
                            // The caller left: nothing failed, and Step6MandatoryFieldsCheck logs it as one line.
                            if (context.RequestAborted.IsCancellationRequested)
                                throw;
                            bool optional = entry.Config.GetValue<bool>("optional", false);
                            if (optional)
                            {
                                this._logger.LogWarning("{time}: Failed to upload files to optional SFTP store ({sftp_store_name})"
                                    + " in Step6FileManagement middleware, continuing"
                                    + Environment.NewLine
                                    + " error: {errorMessage}"
                                    ,
                                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                    entry.Config.Key,
                                    ex.Message
                                    );
                                continue;
                            }
                            this._logger.LogError("{time}: Failed to upload files to SFTP store ({sftp_store_name}) in Step6FileManagement middleware"
                                + Environment.NewLine
                                + " error: {errorMessage}"
                                ,
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                entry.Config.Key,
                                ex.Message
                                );
                            throw; // Re-throw exception to be caught by outer catch
                        }
                    }
                }

                // A caller who left once its files were stored, as the last one was put in place say: the
                // catch below rolls back, and Step6MandatoryFieldsCheck logs it as one line.
                context.RequestAborted.ThrowIfCancellationRequested();

                // Proceed to the next middleware
                await this._next(context);

                if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
                {
                    await RollbackUploadedFilesAsync($"HTTP {context.Response.StatusCode}");
                }
                else
                {
                    await DeleteSetAsideFilesAsync();
                }
            }
            catch (Exception ex)
            {
                // A status the query raised on purpose is logged by Step6MandatoryFieldsCheck, which
                // answers it (at Error when it can no longer reach the client). So is a caller who
                // left, as one line.
                var callerLeft = context.RequestAborted.IsCancellationRequested;
                if (!callerLeft && !Services.DatabaseErrors.TryGetCustomDbError(ex, out _, out _))
                {
                    this._logger.LogError(ex, "{errorCode}: An exception occurred in Step6FileManagement middleware: {message}",
                        _errorCode,
                        LogText.Escape(ex.Message));
                }

                await RollbackUploadedFilesAsync(callerLeft ? "the caller left" : ex.Message);


                // Check if this is a "file already exists" error - return 409 Conflict
                if (ex is FileAlreadyExistsException)
                {
                    this._logger.LogWarning("{time}: File already exists error: {message}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                        LogText.Escape(ex.Message));
                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                // caller friendly message
                                // tell hte caller another file exists by the same name
                                // and advise them to rename the file
                                // don't mention overwrite_existing_files to the caller as it is an internal setting
                                // only DevOps or service providers should be aware of it, hence why we log it above
                                // instead of returning it to the caller.
                                message = "One or more files already exist in the target file store(s). " +
                                    "Please rename the file(s) and try again."
                            }
                        )
                        {
                            StatusCode = 409 // Conflict
                        }
                    );
                    return;
                }

                throw; // Re-throw other exceptions after logging
            }
        }

    }
}
