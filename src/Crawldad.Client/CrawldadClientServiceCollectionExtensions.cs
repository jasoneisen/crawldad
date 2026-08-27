using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Client;

/// <summary>DI registration for the Crawldad client.</summary>
public static class CrawldadClientServiceCollectionExtensions
{
    /// <summary>Registers <see cref="CrawldadClient"/> as a typed <see cref="HttpClient"/> client (so it participates in
    /// <see cref="IHttpClientFactory"/> handler pooling) with the supplied options. The options are validated eagerly, so
    /// a missing base URL or API key fails here at startup rather than on the first request. The returned
    /// <see cref="IHttpClientBuilder"/> lets callers layer message handlers (retries, logging, …) onto the client.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the base URL and API key.</param>
    /// <returns>The HTTP client builder for the registered typed client, for further configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The configured options are missing a base URL or API key.</exception>
    public static IHttpClientBuilder AddCrawldadClient(this IServiceCollection services, Action<CrawldadClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CrawldadClientOptions();
        configure(options);
        var baseUrl = options.Validate();

        services.AddSingleton(options);
        return services.AddHttpClient<CrawldadClient>(client => client.BaseAddress = baseUrl);
    }
}
