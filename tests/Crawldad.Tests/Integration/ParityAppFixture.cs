using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Real;
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

/// <summary>The single collection that hosts EVERY real-Chromium test, co-hosting both harnesses — the fixture-site parity
/// host <see cref="ParityAppFixture"/> and the raw local/remote harness <see cref="RealChromiumFixture"/>. xUnit runs the
/// tests within one collection strictly serially, so at most ONE headless browser is ever driving a scrape.
/// <para>This is one half of the fix for issue #95. The other half is <see cref="Category"/>: on a small hosted CI runner
/// (2–4 cores) a real scrape is starved not by a second browser but by the <em>whole rest of the suite</em> — the other
/// <c>maxParallelThreads</c> lane keeps running Postgres-heavy collections under coverlet instrumentation, saturating the
/// box so a scrape that takes seconds in isolation crosses the product's 120 s synchronous cap. <c>POST /runs</c> then
/// auto-upgrades the still-running run to <c>202 {"status":"running"}</c>, and the tests, which assert <c>200</c>, fail on
/// every CI run. Serializing the browsers here is necessary but not sufficient; CI must also run this category in its own
/// <c>dotnet test</c> invocation with the runner to itself (see <c>ci.yml</c>).</para></summary>
[CollectionDefinition(Name)]
public sealed class RealChromiumCollection : ICollectionFixture<ParityAppFixture>, ICollectionFixture<RealChromiumFixture>
{
    public const string Name = "real-chromium";

    /// <summary>The xUnit trait category every real-Chromium test class carries so CI can split them out: the fast loop
    /// runs <c>Category!=RealChromium</c> in parallel first, then <c>Category=RealChromium</c> alone with the whole runner,
    /// keeping the scrapes synchronous while staying in the one blocking <c>build-test</c> job (coverage is merged across
    /// the two invocations, so the 100% gate is preserved). Mirrors the existing <c>Category=LiveCanary</c> split.</summary>
    public const string Category = "RealChromium";
}
