using Crawldad.Web.Infrastructure.Storage.Azure;
using Crawldad.Web.Infrastructure.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// The storage seam wiring (CD-2, §9.3/§12/§13): the download-sink registry, the provider-selected blob backend (the keyed
/// <see cref="IDownloadSink"/> + the <see cref="IScreenshotStore"/> + the durable <see cref="IRetentionStore"/>), the storage
/// options + boot validation, and the retention janitor. Selecting the backend is data (a config value), exactly like the
/// browser-backend and download-target registries — a new provider is another switch arm, no call-site change.
/// <list type="bullet">
///   <item><see cref="StorageOptions.FakeProvider"/> — the in-memory fake sink + <see cref="InMemoryScreenshotStore"/>; no
///   durable retention store, so the janitor no-ops. The determinism default the test host selects.</item>
///   <item><see cref="StorageOptions.FileSystemProvider"/> — the durable, hermetic <see cref="FileSystemBlobStore"/> backing
///   all three seams (the production default).</item>
///   <item><see cref="StorageOptions.AzureProvider"/> — the durable <see cref="AzureBlobStore"/> (the Azure deployment target).</item>
/// </list>
/// </summary>
public static class StorageModule
{
    /// <summary>Registers the storage seams for the configured provider, plus the options, validation, and retention janitor.</summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">The host configuration (the provider is read from <c>Crawldad:Storage:Provider</c>).</param>
    public static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The knobs + boot-time guard (a non-positive sweep interval / a durable provider missing its target fails at startup).
        services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();

        // The download-sink registry over keyed services (a payload's download.to selects the sink by kind), and the retention
        // janitor (harmless no-op under the fake provider, which registers no IRetentionStore).
        services.AddSingleton<IDownloadSinkRegistry, KeyedDownloadSinkRegistry>();
        services.AddHostedService<RetentionJanitor>();

        // The durable default is filesystem, so an unconfigured production host never silently uses non-durable storage; the
        // test host overrides this to 'fake' (UseCrawldadTestDefaults) for a dependency-free, deterministic suite.
        var provider = configuration[$"{StorageOptions.Section}:Provider"] ?? StorageOptions.FileSystemProvider;
        switch (provider)
        {
            case StorageOptions.FakeProvider:
                services.AddKeyedSingleton<IDownloadSink>(StorageOptions.FakeProvider, static (_, _) => new FakeDownloadSink());
                services.AddSingleton<IScreenshotStore, InMemoryScreenshotStore>();
                break;

            case StorageOptions.FileSystemProvider:
                AddDurableProvider<FileSystemBlobStore>(services, StorageOptions.FileSystemProvider);
                break;

            case StorageOptions.AzureProvider:
                AddDurableProvider<AzureBlobStore>(services, StorageOptions.AzureProvider);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown {StorageOptions.Section}:Provider '{provider}'. Use " +
                    $"'{StorageOptions.FakeProvider}', '{StorageOptions.FileSystemProvider}', or '{StorageOptions.AzureProvider}'.");
        }
    }

    // A durable provider is one class backing all three seams: the keyed download sink (its kind = the provider name), the
    // single screenshot store, and the retention store the janitor sweeps. Registered once and shared across the three.
    private static void AddDurableProvider<TStore>(IServiceCollection services, string kind)
        where TStore : class, IDownloadSink, IScreenshotStore, IRetentionStore
    {
        services.AddSingleton<TStore>();
        services.AddKeyedSingleton<IDownloadSink>(kind, static (sp, _) => sp.GetRequiredService<TStore>());
        services.AddSingleton<IScreenshotStore>(static sp => sp.GetRequiredService<TStore>());
        services.AddSingleton<IRetentionStore>(static sp => sp.GetRequiredService<TStore>());
    }
}
