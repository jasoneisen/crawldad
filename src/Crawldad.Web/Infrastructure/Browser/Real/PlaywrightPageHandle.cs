using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// A thin wrapper over a Playwright <see cref="IPage"/> (§ Deliverable 1) mapping the seam's navigation, waits, network
/// synchronisation, style injection, download, and locator/frame factories 1:1 onto Playwright, with every call guarded
/// by <see cref="PlaywrightFaults"/> so a Playwright timeout/crash surfaces on the §8.3 taxonomy. The
/// <c>waitForRequest</c> primitive arms the wait before running the trigger (Playwright's <c>RunAndWaitForRequestAsync</c>),
/// so a request the trigger provokes is never missed; a request that never fires is a retryable timeout, exactly like
/// the fake.
/// </summary>
/// <param name="page">The wrapped Playwright page.</param>
internal sealed class PlaywrightPageHandle(IPage page) : IPageHandle
{
    public string Url => page.Url;

    public Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct) =>
        PlaywrightFaults.RunAsync(() => page.GotoAsync(
            url, new PageGotoOptions { WaitUntil = PlaywrightMap.WaitUntil(waitUntil), Timeout = PlaywrightMap.Timeout(timeoutMs) }));

    public Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct) =>
        PlaywrightFaults.RunAsync(() => page.WaitForLoadStateAsync(
            PlaywrightMap.LoadState(state), new PageWaitForLoadStateOptions { Timeout = PlaywrightMap.Timeout(timeoutMs) }));

    public ILocatorHandle Locator(string selector) => new PlaywrightLocatorHandle(page.Locator(selector));

    public ILocatorHandle GetByTitle(string title) => new PlaywrightLocatorHandle(page.GetByTitle(title));

    public IFrameHandle FrameLocator(string selector) => new PlaywrightFrameHandle(page.FrameLocator(selector));

    public Task AddStyleTagAsync(string content, CancellationToken ct) =>
        PlaywrightFaults.RunAsync(() => page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = content }));

    public Task RunAndWaitForRequestAsync(Func<Task> trigger, string urlPrefix, string? method, int? timeoutMs, CancellationToken ct) =>
        PlaywrightFaults.RunAsync(() => page.RunAndWaitForRequestAsync(
            trigger,
            request => request.Url.StartsWith(urlPrefix, StringComparison.Ordinal)
                && (method is null || string.Equals(request.Method, method, StringComparison.Ordinal)),
            new PageRunAndWaitForRequestOptions { Timeout = PlaywrightMap.Timeout(timeoutMs) }));

    public Task<IDownloadHandle> RunAndWaitForDownloadAsync(Func<Task> trigger, int? timeoutMs, CancellationToken ct) =>
        PlaywrightFaults.RunAsync<IDownloadHandle>(async () =>
        {
            var download = await page.RunAndWaitForDownloadAsync(
                trigger, new PageRunAndWaitForDownloadOptions { Timeout = PlaywrightMap.Timeout(timeoutMs) });
            return new PlaywrightDownloadHandle(download);
        });

    public Task CloseAsync(CancellationToken ct) => PlaywrightFaults.RunAsync(() => page.CloseAsync());
}
