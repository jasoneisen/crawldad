using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Real;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The Phase 4 WP2 parity host: one Alba host shared by both real-Chromium parity classes (built once, its schema
/// migrated once, its browser launched once). It is the ordinary product host with two DI overrides layered on the
/// shared single-node test defaults: the pinned <see cref="FakeClock"/> (deterministic run traces) and — the WP2 seam —
/// the <c>"local"</c> browser adapter swapped for the <see cref="FixtureChromiumBackend"/>, so the acceptance payloads
/// bind <c>inputs.backend = { adapter: "local" }</c> and run through the real <c>POST /runs</c> path and interpreter
/// against real headless Chromium served entirely from the local fixture corpus. The Playwright driver is the product
/// singleton; the fixture backend launches and owns the one shared browser and is disposed with the host.
/// </summary>
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

                // The WP2 override: the "local" adapter becomes the fixture-site-backed real-Chromium backend (last
                // keyed registration wins), so the canonical acceptance payloads execute against real Chromium with no
                // live traffic. The shared Playwright driver is the product host singleton.
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
