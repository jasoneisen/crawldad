using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>A thin wrapper over a Playwright <see cref="ILocator"/>: refinements stay lazy and touch no DOM. Read
/// terminals short-circuit to null/empty on a zero <see cref="CountAsync"/> instead of blocking through Playwright's
/// auto-wait timeout; actions (click/fill/clear/waitFor) are pure passthroughs that keep the auto-wait.</summary>
internal sealed class PlaywrightLocatorHandle(ILocator locator) : ILocatorHandle
{
    public ILocatorHandle Locator(string childSelector) => new PlaywrightLocatorHandle(locator.Locator(childSelector));

    public ILocatorHandle Nth(int index) => new PlaywrightLocatorHandle(locator.Nth(index));

    public ILocatorHandle First => new PlaywrightLocatorHandle(locator.First);

    // Playwright evaluates this regex in the browser and rejects .NET-specific options like CultureInvariant, so
    // this uses GuardedRegex's browser-safe compile variant — the size cap still bounds the pattern.
    public ILocatorHandle Filter(string hasTextRegex) =>
        new PlaywrightLocatorHandle(locator.Filter(new LocatorFilterOptions { HasTextRegex = GuardedRegex.CompileForBrowser(hasTextRegex) }));

    public Task<int> CountAsync(CancellationToken ct) => PlaywrightFaults.RunAsync(() => locator.CountAsync());

    public Task<string?> TextContentAsync(CancellationToken ct) =>
        PlaywrightFaults.RunAsync(async () => await locator.CountAsync() == 0 ? null : await locator.First.TextContentAsync());

    public Task<string> InnerTextAsync(CancellationToken ct) =>
        PlaywrightFaults.RunAsync(async () => await locator.CountAsync() == 0 ? string.Empty : await locator.First.InnerTextAsync());

    public Task<string> InnerHTMLAsync(CancellationToken ct) =>
        PlaywrightFaults.RunAsync(async () => await locator.CountAsync() == 0 ? string.Empty : await locator.First.InnerHTMLAsync());

    public Task<string?> GetAttributeAsync(string name, CancellationToken ct) =>
        PlaywrightFaults.RunAsync(async () => await locator.CountAsync() == 0 ? null : await locator.First.GetAttributeAsync(name));

    public Task ClickAsync(int? timeoutMs, CancellationToken ct) =>
        PlaywrightFaults.RunAsync(() => locator.ClickAsync(new LocatorClickOptions { Timeout = PlaywrightMap.Timeout(timeoutMs) }));

    public Task FillAsync(string value, CancellationToken ct) => PlaywrightFaults.RunAsync(() => locator.FillAsync(value));

    public Task ClearAsync(CancellationToken ct) => PlaywrightFaults.RunAsync(() => locator.ClearAsync());

    public Task WaitForAsync(string state, int? timeoutMs, CancellationToken ct) =>
        PlaywrightFaults.RunAsync(() => locator.WaitForAsync(
            new LocatorWaitForOptions { State = PlaywrightMap.WaitForState(state), Timeout = PlaywrightMap.Timeout(timeoutMs) }));
}
