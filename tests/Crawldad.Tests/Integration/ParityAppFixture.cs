using Alba;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Browser.Real;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>One Alba host shared by both real-Chromium parity classes (built once, schema migrated once, browser
/// launched once), with two DI overrides: a pinned <see cref="FakeClock"/> and the <c>"local"</c> browser adapter
/// swapped for <see cref="FixtureChromiumBackend"/>, so acceptance payloads run against real Chromium, no live traffic.</summary>
public sealed class ParityAppFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_parity");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeClock());

                // This override: the "local" adapter becomes the fixture-site-backed real-Chromium backend (last keyed
                // registration wins), so the canonical acceptance payloads execute against real Chromium with no live traffic.
                // The shared Playwright driver is the product host singleton.
                services.AddKeyedSingleton<IBrowserBackend>("local", static (sp, _) =>
                    new FixtureChromiumBackend(sp.GetRequiredService<IPlaywrightProvider>(), Runner.FixturesRoot));
            });
        })).AuthenticatedAsPrimaryTenant();

        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>Serializes both real-Chromium parity classes onto the one shared parity host + browser.</summary>
[CollectionDefinition(Name)]
public sealed class RealChromiumParityCollection : ICollectionFixture<ParityAppFixture>
{
    public const string Name = "real-chromium-parity";
}
