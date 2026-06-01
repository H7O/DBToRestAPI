using Com.H.Threading;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DBToRestAPI.Services;

/// <summary>
/// Serves static content as a <b>fallback</b> after API resolution has failed.
///
/// The engine is API-first: <see cref="Middlewares.Step1ServiceTypeChecks"/> resolves API
/// gateway routes and database-query routes first; only when both miss does it call
/// <see cref="TryServeAsync"/>. This preserves the priority order
/// <c>api_gateway → db_query → static file → 404</c>.
///
/// Rather than hand-rolling file streaming, this service reuses ASP.NET Core's
/// <see cref="StaticFileMiddleware"/> (Range requests, ETag/Last-Modified, conditional GET,
/// correct content types) and <see cref="DefaultFilesMiddleware"/> (index document), wired over a
/// <see cref="PhysicalFileProvider"/> rooted at the configured folder. The file provider rooting is
/// itself the directory-traversal defense: paths that escape the root resolve to
/// <c>NotFound</c>, and dot/hidden/system files are excluded by default.
///
/// Configuration lives under the <c>static_files</c> block in <c>settings.xml</c> and is reloaded
/// automatically when the file changes, mirroring <see cref="QueryRouteResolver"/> /
/// <see cref="RouteConfigResolver"/>.
///
/// <code>
/// &lt;static_files&gt;
///   &lt;root_path&gt;&lt;![CDATA[./web/]]&gt;&lt;/root_path&gt;
///   &lt;default&gt;index.html,index.htm&lt;/default&gt;
///   &lt;!-- optional --&gt;
///   &lt;enabled&gt;true&lt;/enabled&gt;
///   &lt;cache_control_max_age_seconds&gt;3600&lt;/cache_control_max_age_seconds&gt;
///   &lt;serve_unknown_file_types&gt;false&lt;/serve_unknown_file_types&gt;
///   &lt;spa_fallback&gt;false&lt;/spa_fallback&gt;
/// &lt;/static_files&gt;
/// </code>
/// </summary>
public sealed class StaticFileFallbackService
{
    private const string MissKey = "__static_file_miss";

    /// <summary>Immutable snapshot of the active configuration, swapped atomically on reload.</summary>
    private sealed record StaticState(
        bool Enabled,
        RequestDelegate? Pipeline,
        bool SpaFallback,
        string SpaFallbackDocument,
        PhysicalFileProvider? Provider,
        string? RootPath);

    private static readonly StaticState Disabled =
        new(false, null, false, "index.html", null, null);

    private readonly IEncryptedConfiguration _configuration;
    private readonly IWebHostEnvironment _env;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<StaticFileFallbackService> _logger;
    private readonly AtomicGate _reloadingGate = new();

    private volatile StaticState _state = Disabled;

    public StaticFileFallbackService(
        IEncryptedConfiguration configuration,
        IWebHostEnvironment env,
        ILoggerFactory loggerFactory,
        ILogger<StaticFileFallbackService> logger)
    {
        _configuration = configuration;
        _env = env;
        _loggerFactory = loggerFactory;
        _logger = logger;

        Rebuild(); // initial load
        ChangeToken.OnChange(
            () => _configuration.GetSection("static_files").GetReloadToken(),
            Rebuild);
    }

    /// <summary>Whether static serving is currently enabled (a valid root folder is configured).</summary>
    public bool IsEnabled => _state.Enabled;

    /// <summary>
    /// Attempts to serve the request from the configured static root.
    /// Returns <c>true</c> if a file (or the SPA fallback document) was written to the response,
    /// in which case the caller must stop processing. Returns <c>false</c> if static serving is
    /// disabled, the verb is not GET/HEAD, or no matching file exists — leaving the response
    /// untouched so the caller can emit its normal 404.
    /// </summary>
    public async Task<bool> TryServeAsync(HttpContext context)
    {
        var state = _state;
        if (!state.Enabled || state.Pipeline is null)
            return false;

        // Static content is read-only: only GET/HEAD are eligible.
        // (StaticFileMiddleware enforces this too; checking here avoids needless endpoint juggling.)
        if (!HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method))
            return false;

        // StaticFileMiddleware/DefaultFilesMiddleware deliberately refuse to act when an endpoint
        // is already selected — and routing has already matched our catch-all controller endpoint.
        // Clear it so the static engine will serve; restore it on a miss for hygiene.
        var originalEndpoint = context.GetEndpoint();
        context.SetEndpoint(null);
        context.Items.Remove(MissKey);

        try
        {
            await state.Pipeline(context);

            if (!context.Items.ContainsKey(MissKey))
                return true; // a file was served

            // Miss. Optionally serve the SPA fallback document for browser navigation requests
            // (GET + Accept: text/html) so a client-side router can take over the path.
            if (state.SpaFallback
                && HttpMethods.IsGet(context.Request.Method)
                && AcceptsHtml(context.Request)
                && !context.Response.HasStarted)
            {
                context.Items.Remove(MissKey);
                var originalPath = context.Request.Path;
                context.Request.Path = "/" + state.SpaFallbackDocument;
                try
                {
                    await state.Pipeline(context);
                }
                finally
                {
                    context.Request.Path = originalPath;
                }

                if (!context.Items.ContainsKey(MissKey))
                    return true;
            }
        }
        finally
        {
            context.Items.Remove(MissKey);
        }

        // Nothing served — restore the endpoint and let the caller emit its 404.
        context.SetEndpoint(originalEndpoint);
        return false;
    }

    private static bool AcceptsHtml(HttpRequest request)
    {
        foreach (var accept in request.Headers.Accept)
        {
            if (accept is not null
                && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void Rebuild()
    {
        if (!_reloadingGate.TryOpen()) return;

        var previousProvider = _state.Provider;
        try
        {
            var section = _configuration.GetSection("static_files");
            if (!section.Exists())
            {
                _state = Disabled;
                return;
            }

            // Explicit opt-out without deleting the block.
            if (section.GetValue<bool?>("enabled") == false)
            {
                _logger.LogInformation("Static file serving is disabled (static_files:enabled = false).");
                _state = Disabled;
                return;
            }

            var configuredRoot = section.GetValue<string>("root_path");
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                _logger.LogWarning(
                    "A `static_files` block is present but `root_path` is empty. Static file serving is disabled.");
                _state = Disabled;
                return;
            }

            var root = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(AppContext.BaseDirectory, configuredRoot);
            root = Path.GetFullPath(root);

            // Security guard: refuse a root that IS, or is an ANCESTOR of, the application base
            // directory. Such a root would expose config/*.xml, appsettings*.json, demo.db, etc.
            // A dedicated subfolder (e.g. ./web/) lives under the base dir but is not an ancestor
            // of it, so it passes — and PhysicalFileProvider then makes ../config unreachable.
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            if (IsSameOrAncestorOf(root, baseDir))
            {
                _logger.LogError(
                    "static_files:root_path `{Root}` resolves to the application base directory (or an ancestor of it). "
                    + "Serving it would expose configuration and other sensitive files, so static serving is DISABLED. "
                    + "Point root_path at a dedicated subfolder such as ./web/.", root);
                _state = Disabled;
                return;
            }

            // Defense-in-depth: also refuse a root that IS, or sits INSIDE, a folder the application
            // reads secrets from — the conventional `config/` directory and the configured DPAPI
            // data-protection key ring. These are descendants of the base directory (so the ancestor
            // check above does not catch them), yet they hold connection strings, API keys,
            // auth-provider secrets, and the key material used to decrypt them. A dedicated assets
            // folder such as ./web/ is disjoint from these and still passes.
            foreach (var sensitiveDir in GetSensitiveDirectories(baseDir))
            {
                if (IsSameOrAncestorOf(sensitiveDir, root))
                {
                    _logger.LogError(
                        "static_files:root_path `{Root}` is, or is nested under, a sensitive directory (`{Sensitive}`). "
                        + "Serving it would expose configuration files and/or encryption keys, so static serving is DISABLED. "
                        + "Point root_path at a dedicated assets folder such as ./web/.", root, sensitiveDir);
                    _state = Disabled;
                    return;
                }
            }

            if (!Directory.Exists(root))
            {
                _logger.LogWarning(
                    "static_files:root_path `{Root}` does not exist. Static file serving is disabled until the folder "
                    + "is created and the configuration is reloaded (or the app restarts).", root);
                _state = Disabled;
                return;
            }

            var defaultDocs = ParseDefaults(section.GetValue<string>("default"));
            var serveUnknown = section.GetValue<bool?>("serve_unknown_file_types") ?? false;
            var maxAge = section.GetValue<int?>("cache_control_max_age_seconds");
            var spaFallback = section.GetValue<bool?>("spa_fallback") ?? false;
            var spaDocument = defaultDocs.Length > 0 ? defaultDocs[0] : "index.html";

            var provider = new PhysicalFileProvider(root);
            var pipeline = BuildPipeline(provider, defaultDocs, serveUnknown, maxAge);

            _state = new StaticState(true, pipeline, spaFallback, spaDocument, provider, root);

            _logger.LogInformation(
                "Static file serving enabled. Root: `{Root}`; default documents: [{Defaults}]; "
                + "serve_unknown_file_types: {ServeUnknown}; spa_fallback: {Spa}.",
                root, string.Join(", ", defaultDocs), serveUnknown, spaFallback);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to (re)build static file serving configuration. Static serving disabled.");
            _state = Disabled;
        }
        finally
        {
            // Dispose the superseded provider (releases its file watcher) if it was replaced.
            if (previousProvider is not null && !ReferenceEquals(previousProvider, _state.Provider))
            {
                try { previousProvider.Dispose(); } catch { /* best effort */ }
            }
            _reloadingGate.TryClose();
        }
    }

    /// <summary>
    /// Builds the reusable sub-pipeline: DefaultFiles (index document) → StaticFiles (serve) →
    /// terminal that flags a miss. The middlewares are constructed directly (their constructors are
    /// public) so we don't need an IApplicationBuilder, and we supply our own FileProvider so no
    /// wwwroot is required.
    /// </summary>
    private RequestDelegate BuildPipeline(
        IFileProvider provider,
        string[] defaultDocs,
        bool serveUnknown,
        int? maxAge)
    {
        // Terminal: reached only when neither middleware served the request.
        RequestDelegate terminal = ctx =>
        {
            ctx.Items[MissKey] = true;
            return Task.CompletedTask;
        };

        var staticOptions = new StaticFileOptions
        {
            FileProvider = provider,
            ServeUnknownFileTypes = serveUnknown,
            RedirectToAppendTrailingSlash = false,
        };
        if (maxAge is int seconds && seconds >= 0)
        {
            staticOptions.OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = $"public, max-age={seconds}";
            };
        }

        var staticMiddleware = new StaticFileMiddleware(
            terminal, _env, Options.Create(staticOptions), _loggerFactory);

        var defaultFilesOptions = new DefaultFilesOptions
        {
            FileProvider = provider,
            RedirectToAppendTrailingSlash = false,
        };
        defaultFilesOptions.DefaultFileNames.Clear();
        foreach (var doc in defaultDocs)
            defaultFilesOptions.DefaultFileNames.Add(doc);

        var defaultFilesMiddleware = new DefaultFilesMiddleware(
            ctx => staticMiddleware.Invoke(ctx), _env, Options.Create(defaultFilesOptions));

        return ctx => defaultFilesMiddleware.Invoke(ctx);
    }

    private static string[] ParseDefaults(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ["index.html", "index.htm"];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Directories the application reads secrets from, which must never fall inside the static root:
    /// the conventional <c>config/</c> folder (settings.xml, sql.xml, api_keys.xml, auth_providers.xml, …)
    /// and the configured DPAPI data-protection key ring. Paths are returned fully resolved; they are
    /// only compared, so they need not exist on disk.
    /// </summary>
    private IEnumerable<string> GetSensitiveDirectories(string baseDir)
    {
        yield return Path.GetFullPath(Path.Combine(baseDir, "config"));

        var keyPath = _configuration.GetValue<string>("settings_encryption:data_protection_key_path");
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            var resolved = Path.IsPathRooted(keyPath) ? keyPath : Path.Combine(baseDir, keyPath);
            yield return Path.GetFullPath(resolved);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is the same as, or nested under,
    /// <paramref name="potentialAncestor"/> — i.e. <paramref name="potentialAncestor"/> is an
    /// ancestor-or-equal of <paramref name="path"/>.
    /// </summary>
    private static bool IsSameOrAncestorOf(string potentialAncestor, string path)
    {
        var ancestor = WithTrailingSeparator(Path.GetFullPath(potentialAncestor));
        var candidate = WithTrailingSeparator(Path.GetFullPath(path));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.StartsWith(ancestor, comparison);
    }

    private static string WithTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
