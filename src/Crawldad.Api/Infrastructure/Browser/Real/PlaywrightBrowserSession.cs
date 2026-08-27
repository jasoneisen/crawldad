using Microsoft.Playwright;

namespace Crawldad.Api.Infrastructure.Browser.Real;

/// <summary>The shared real-backend session: wraps one Playwright <see cref="IBrowserContext"/> and applies the route
/// policy to every page it opens. Disposal always closes the context; when <paramref name="ownedBrowser"/> is
/// non-null (remote adapters) disposal also closes that connection, but the local adapter shares one browser and passes null.</summary>
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

    // Per-request route policy: block, serve from cache, or pass through the throttle.
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
