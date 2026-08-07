using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// A thin wrapper over a Playwright <see cref="ILocator"/> (§ Deliverable 1). Playwright locators are natively lazy —
/// a query, re-evaluated on every terminal — so the refinements (<see cref="Locator"/>/<see cref="Nth"/>/
/// <see cref="First"/>/<see cref="Filter"/>) just wrap the narrowed locator and touch no DOM, preserving the seam's
/// re-query-on-use contract. Every terminal is guarded by <see cref="PlaywrightFaults"/> so a Playwright timeout/crash
/// maps onto the §8.3 taxonomy.
/// <para>
/// The read terminals honour the seam's <b>null/empty-if-absent</b> contract (<see cref="ILocatorHandle"/>): rather
/// than let Playwright's auto-wait block for the full timeout on a locator that matches nothing, they short-circuit on
/// a zero <see cref="CountAsync"/> and otherwise read the <see cref="First"/> match — matching the record/replay fake
/// so <c>fake ≡ real</c> holds for the acceptance payloads. The <b>actions</b> (click/fill/clear/waitFor) are pure
/// passthroughs that keep Playwright's auto-wait — which the reference relies on across postbacks.
/// </para>
/// </summary>
/// <param name="locator">The wrapped Playwright locator (already lazy).</param>
internal sealed class PlaywrightLocatorHandle(ILocator locator) : ILocatorHandle
{
    public ILocatorHandle Locator(string childSelector) => new PlaywrightLocatorHandle(locator.Locator(childSelector));

    public ILocatorHandle Nth(int index) => new PlaywrightLocatorHandle(locator.Nth(index));

    public ILocatorHandle First => new PlaywrightLocatorHandle(locator.First);

    // Size-guarded through the same GuardedRegex factory the expression builtins use (§7.2). Playwright matches the
    // regex in the browser and rejects .NET-specific options (e.g. CultureInvariant), so we use the option-free
    // browser-safe variant — the size cap remains the language-boundary guarantee.
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
