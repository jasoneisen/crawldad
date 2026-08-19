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

/// <summary>The single collection that hosts EVERY real-Chromium test. xUnit runs the tests within one collection strictly
/// serially, so co-hosting both real-Chromium harnesses here — the fixture-site parity host <see cref="ParityAppFixture"/>
/// and the raw local/remote harness <see cref="RealChromiumFixture"/> — guarantees at most ONE headless browser is ever
/// driving a scrape. That is the fix for issue #95: when the two harnesses lived in separate collections they ran in
/// parallel (<c>maxParallelThreads: 2</c>) and starved each other badly enough (~80x) that a fixture scrape — seconds in
/// isolation — crossed the product's 120 s synchronous cap, so <c>POST /runs</c> auto-upgraded the still-running run to
/// <c>202 {"status":"running"}</c> and the tests, which asserted <c>200</c>, failed on every CI run. Light collections
/// still parallelize against this one, so overall feedback stays fast. (Per-collection <c>DisableParallelization</c> is
/// not honoured by the VSTest runner used here, so co-hosting on one collection is the reliable lever.)</summary>
[CollectionDefinition(Name)]
public sealed class RealChromiumCollection : ICollectionFixture<ParityAppFixture>, ICollectionFixture<RealChromiumFixture>
{
    public const string Name = "real-chromium";
}
