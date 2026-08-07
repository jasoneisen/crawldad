using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// The shared real-backend session (§9.2): wraps one Playwright <see cref="IBrowserContext"/> and applies the §8.1
/// route policy to every page it opens via <c>page.RouteAsync</c> — reproducing <c>PlaywrightFactory</c>'s route block
/// on top of whatever context the adapter handed back, so the local/browserless/browserbase adapters share it verbatim.
/// <para>
/// The route handler mirrors the reference exactly: <b>abort</b> a blocked host or resource type; else <b>serve</b> a
/// cacheable asset from the cross-run <see cref="IAssetCache"/> (fetch-and-store on a miss, fulfil on a hit, counting
/// the hit into <see cref="CacheHits"/>); else pass through the global <see cref="IThrottleGate"/> and continue.
/// </para>
/// <para>
/// Disposal closes the context (tearing down this run's pages). A remote adapter also owns the underlying
/// <see cref="IBrowser"/> connection — passed as <paramref name="ownedBrowser"/> — and disposal closes it too; the
/// local adapter shares one long-lived browser and passes null, so only the context is torn down (§12 per-run isolation
/// via contexts).
/// </para>
/// </summary>
/// <param name="context">The context pages are opened on.</param>
/// <param name="ownedBrowser">The connection to dispose with the session (remote adapters), or null when the browser is shared (local adapter).</param>
/// <param name="policy">The §8.1 launch/context/route policy (only the route block is applied here; launch/context were applied at connect).</param>
/// <param name="cache">The cross-run asset cache backing the route cache.</param>
/// <param name="throttle">The global request throttle for non-cached requests.</param>
/// <param name="region">The backend region this session runs in (cache-locality tag, §8.1).</param>
internal sealed class PlaywrightBrowserSession(
    IBrowserContext context,
    IBrowser? ownedBrowser,
    SessionPolicy policy,
    IAssetCache cache,
    IThrottleGate throttle,
    string region) : IBrowserSession
{
    private int _cacheHits;

    public string Region => region;

    public int CacheHits => _cacheHits;

    public async Task<IPageHandle> NewPageAsync(CancellationToken ct)
    {
        var page = await PlaywrightFaults.RunAsync(() => context.NewPageAsync());
        await page.RouteAsync("**/*", route => HandleRouteAsync(route, ct));
        return new PlaywrightPageHandle(page);
    }

    // The §8.1 route policy, applied per intercepted request exactly as PlaywrightFactory.CreateBrowserContext does.
    private async Task HandleRouteAsync(IRoute route, CancellationToken ct)
    {
        var request = route.Request;
        switch (policy.Route.Classify(request.Url, request.ResourceType))
        {
            case RouteDisposition.Block:
                await route.AbortAsync();
                break;

            case RouteDisposition.Cache:
                var lookup = await cache.GetOrAddAsync(region, request.Url, async () =>
                {
                    var response = await route.FetchAsync();
                    return new CachedAsset(await response.BodyAsync(), response.Headers, response.Status);
                });
                if (lookup.Hit)
                {
                    Interlocked.Increment(ref _cacheHits);
                }

                await route.FulfillAsync(new RouteFulfillOptions
                {
                    BodyBytes = lookup.Asset.Body,
                    Headers = lookup.Asset.Headers,
                    Status = lookup.Asset.Status,
                });
                break;

            default:
                await throttle.WaitAsync(policy.Route.ThrottleMinIntervalMs, ct);
                await route.ContinueAsync();
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await context.CloseAsync();
        if (ownedBrowser is not null)
        {
            await ownedBrowser.DisposeAsync();
        }
    }
}
