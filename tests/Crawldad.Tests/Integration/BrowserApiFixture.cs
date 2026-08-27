using Alba;
using Crawldad.Api;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>A shared Alba host for the browsers-API suite (its own Marten schema). Seeds one tenant-namespaced config
/// secret so the connect resolver's <c>Secrets:{tenant}:{ref}</c> fallback has a value to resolve. Default auth is the
/// primary tenant; isolation tests override per scenario with the secondary key.</summary>
public sealed class BrowserApiFixture : IAsyncLifetime
{
    /// <summary>A ref that exists ONLY in the primary tenant's config vault (no registered browser) — the fallback path.</summary>
    internal const string ConfigFallbackRef = "config-fallback-cred";

    /// <summary>The value <see cref="ConfigFallbackRef"/> resolves to via the config fallback.</summary>
    internal const string ConfigFallbackSecret = "cfg_ONLY_secret_0123456789abcdef";

    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_browsers");
            builder.UseSetting($"Secrets:{TestTenants.PrimaryId}:{ConfigFallbackRef}", ConfigFallbackSecret);
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new FakeClock()));
        })).AuthenticatedAsPrimaryTenant();
        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>The browsers-API collection — one shared host, sequential tests, each resetting Marten data first.</summary>
[CollectionDefinition(Name)]
public sealed class BrowserApiCollection : ICollectionFixture<BrowserApiFixture>
{
    public const string Name = "browser-api";
}
