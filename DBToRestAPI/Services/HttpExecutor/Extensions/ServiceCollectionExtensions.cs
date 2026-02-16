using Microsoft.Extensions.DependencyInjection;

namespace DBToRestAPI.Services.HttpExecutor.Extensions;

/// <summary>
/// Extension methods for registering HttpRequestExecutor services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the HTTP Request Executor service to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHttpRequestExecutor(
        this IServiceCollection services,
        Action<HttpExecutorOptions>? configure = null)
    {
        var options = new HttpExecutorOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);

        // Standard client
        services.AddHttpClient("HttpExecutor")
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        // Client that ignores certificate errors
        services.AddHttpClient("HttpExecutor.IgnoreCerts")
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });

        // Client that doesn't follow redirects
        services.AddHttpClient("HttpExecutor.NoRedirect")
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });

        // Combined: ignore certs + no redirect
        services.AddHttpClient("HttpExecutor.IgnoreCerts.NoRedirect")
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                AllowAutoRedirect = false
            });

        services.AddSingleton<IHttpRequestExecutor, HttpRequestExecutor>();

        return services;
    }
}
