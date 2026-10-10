using Com.H.Cache;
using DBToRestAPI.Cache;
using DBToRestAPI.Middlewares;
using DBToRestAPI.Services;
using DBToRestAPI.Services.HttpExecutor.Extensions;
using DBToRestAPI.Services.QueryParser;
using DBToRestAPI.Settings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.SqlClient;
using System.Data.Common;



var builder = WebApplication.CreateBuilder(args);


// The documented order, each source overriding the ones before it: appsettings.json,
// appsettings.{Environment}.json, settings.xml, the additional files, environment variables,
// command-line arguments. The default sources read both JSON files from the working directory (the
// content root, where Kestrel also resolves a relative certificate path). appsettings.json is also
// read from the executable's folder, as the lowest layer, so it applies whatever the working directory.
builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory),
    Path = "appsettings.json",
    Optional = false,
    ReloadOnChange = true,
});
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddResilientXmlFile("config/settings.xml", optional: false, reloadOnChange: true)
    // Load additional configuration files specified in "additional_configurations:path"
    .AddDynamicConfigurationFiles(builder.Configuration)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    ;


// builder.Configuration.AddDynamicConfigurationFiles(builder.Configuration);


// Add services to the container.

// Settings encryption service - must be registered early to decrypt config values
// before other services initialize. Only active on Windows (uses DPAPI).
// Register as both the concrete type and interface for flexibility
builder.Services.AddSingleton<SettingsEncryptionService>();
builder.Services.AddSingleton<IEncryptedConfiguration>(sp => sp.GetRequiredService<SettingsEncryptionService>());



builder.Services.AddSingleton<DbConnectionFactory>();


builder.Services.AddScoped<TempFilesTracker>();


builder.Services.AddHybridCache();

builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<CacheService>();

builder.Services.AddSingleton<SettingsService>();

builder.Services.AddSingleton<RouteConfigResolver>();

builder.Services.AddSingleton<QueryRouteResolver>();

// Indexes OIDC providers (issuer/audience) so an endpoint can allow multiple providers.
// Consumed by Step4JwtAuthorization. See MULTI_PROVIDER_OIDC.md.
builder.Services.AddSingleton<OidcProviderIndex>();

builder.Services.AddSingleton<ParametersBuilder>();

// Serves static content (web/ folder) as a fallback when no API route matches.
// Driven by the optional `static_files` block in settings.xml; no-op when absent.
builder.Services.AddSingleton<StaticFileFallbackService>();

builder.Services.AddSingleton<ApiKeysService>();

builder.Services.AddSingleton<IQueryConfigurationParser, QueryConfigurationParser>();

builder.Services.AddSingleton<OpenApiDocumentBuilder>();






// builder.Services.AddSingleton<IConfiguration>(builder.Configuration);

builder.Services.AddHttpClient();

// OIDC discovery documents and signing keys, and files a download query names by URL: pooled
// connections (without cookies, below), and clients that tests answer in process.
builder.Services.AddHttpClient(Step4JwtAuthorization.OidcMetadataClient);
builder.Services.AddHttpClient(DBToRestAPI.Controllers.ApiController.FileDownloadClient);

builder.Services.AddHttpClient("checkCertificateErrors")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
    });

builder.Services.AddHttpClient("ignoreCertificateErrors", c =>
{
    // No additional configuration required here
})
.ConfigureHttpClient(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    return new HttpClientHandler
    {
        // Ignore certificate errors
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    };
});

// HTTP Request Executor service - curl-like HTTP client with JSON configuration
builder.Services.AddHttpRequestExecutor(options =>
{
    options.DefaultTimeoutSeconds = 30;
    options.EnableRequestLogging = true;
});

// The engine's HTTP clients serve every caller, so none of them keeps cookies: a pooled handler would
// send a cookie a remote service set while serving one caller with the next caller's request. A Cookie
// header the request carries itself (a gateway caller's, say) is still sent. Registered after every
// client, so it runs after each client has chosen its handler.
builder.Services.ConfigureAll<Microsoft.Extensions.Http.HttpClientFactoryOptions>(options =>
    options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
    {
        switch (handlerBuilder.PrimaryHandler)
        {
            case HttpClientHandler handler: handler.UseCookies = false; break;
            case SocketsHttpHandler handler: handler.UseCookies = false; break;
        }
    }));


builder.Services.AddControllers();


var maxFileSize = builder.Configuration.GetValue<long?>("max_payload_size_in_bytes")
    ?? (300 * 1024 * 1024); // Default to 300MB if not found


// Gracefully skip HTTPS endpoint if the configured certificate file is missing
var httpsCertPath = builder.Configuration["Kestrel:Endpoints:Https:Certificate:Path"];
var httpsSkipped = false;
string? resolvedCertPath = null;
if (!string.IsNullOrEmpty(httpsCertPath))
{
    // Where Kestrel loads it from: a relative path is under the content root (the working directory).
    resolvedCertPath = Path.IsPathRooted(httpsCertPath)
        ? httpsCertPath
        : Path.Combine(builder.Environment.ContentRootPath, httpsCertPath);

    if (!File.Exists(resolvedCertPath))
    {
        httpsSkipped = true;
        // Replace Kestrel's configuration loader with an HTTP-only section.
        // Just adding programmatic endpoints via ListenAnyIP() doesn't prevent
        // the default config loader from also processing the HTTPS section.
        var httpUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://*:5000";
        var httpOnlyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Endpoints:Http:Url"] = httpUrl,
            })
            .Build();
        builder.WebHost.UseKestrel(serverOptions =>
        {
            serverOptions.Configure(httpOnlyConfig, reloadOnChange: false);
        });
    }
}

// Set maximum request body size for Kestrel and form options
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = maxFileSize;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxFileSize;
});


// Monitor configuration path changes
builder.Services.AddHostedService<ConfigurationPathMonitor>();

var app = builder.Build();


app.UseHttpsRedirection();

app.UseHsts();

app.UseAuthorization();

app.MapControllers();

app.UseMiddleware<OpenApiMiddleware>();              // 0. OpenAPI spec (short-circuit, no pipeline overhead)
app.UseMiddleware<Step1ServiceTypeChecks>();        // 1. Route resolution & service type determination
app.UseMiddleware<Step2CorsCheck>();                // 2. CORS headers (must be before auth for preflight)
app.UseMiddleware<Step3ApiKeysCheck>();             // 3. Local API key validation
app.UseMiddleware<Step4JwtAuthorization>();         // 4. JWT/OAuth 2.0 validation
app.UseMiddleware<Step4bRateLimiting>();            // 4b. Per-endpoint rate limiting (after auth so the caller can be the user; before the gateway so proxied routes are covered)
app.UseMiddleware<Step5APIGatewayProcess>();        // 5. API Gateway proxy
app.UseMiddleware<Step6MandatoryFieldsCheck>();     // 6. Parameter validation
app.UseMiddleware<Step7FileUploadManagement>();     // 7. File upload processing
app.UseMiddleware<Step8FileDownloadManagement>();   // 8. File download processing


// Log the URLs/ports the server is listening on after startup
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    var addresses = app.Urls.ToList();
    
    if (!addresses.Any())
    {
        // Fallback: get from server features if app.Urls is empty
        var serverAddresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        if (serverAddresses?.Any() == true)
        {
            addresses = serverAddresses.ToList();
        }
    }

    if (addresses.Any())
    {
        logger.LogInformation("");
        logger.LogInformation("╔════════════════════════════════════════════════════════════════╗");
        logger.LogInformation("║  🚀 DB-to-REST API is up and running!                          ║");
        logger.LogInformation("╠════════════════════════════════════════════════════════════════╣");
        foreach (var address in addresses)
        {
            var paddedAddress = $"║  ➜  {address}".PadRight(65) + "║";
            logger.LogInformation("{Address}", paddedAddress);
        }
        logger.LogInformation("╚════════════════════════════════════════════════════════════════╝");
        logger.LogInformation("");
    }

    if (httpsSkipped)
    {
        logger.LogWarning("HTTPS endpoint skipped — certificate not found at '{CertPath}'. Place your .pfx certificate there and restart to enable HTTPS. See docs/topics/16-tls-certificates.md", resolvedCertPath);
    }
});

app.Run();