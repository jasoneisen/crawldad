using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// Translates the seam's string/int arguments into the Playwright option enums the wrappers pass through (§9). Kept in
/// one place so the mapping is uniform and unit-testable without a browser: the seam's load/wait-state vocabularies
/// (§5.1) map 1:1 onto Playwright's enums, and a per-call timeout in milliseconds becomes Playwright's nullable
/// <c>float</c> (null ⇒ the context default).
/// </summary>
internal static class PlaywrightMap
{
    /// <summary>A per-call timeout override in milliseconds as Playwright's nullable float; null ⇒ the context default (§8.4).</summary>
    /// <param name="ms">The override in milliseconds, or null.</param>
    public static float? Timeout(int? ms) => ms.HasValue ? ms.Value : null;

    /// <summary>The <c>goto</c> node's wait-until (§5.1) as a Playwright <see cref="WaitUntilState"/>; null/unknown ⇒ the backend default.</summary>
    /// <param name="waitUntil">The load state to await, or null.</param>
    public static WaitUntilState? WaitUntil(string? waitUntil) => waitUntil switch
    {
        "load" => WaitUntilState.Load,
        "domcontentloaded" => WaitUntilState.DOMContentLoaded,
        "networkidle" => WaitUntilState.NetworkIdle,
        "commit" => WaitUntilState.Commit,
        _ => null,
    };

    /// <summary>The <c>waitForLoadState</c> node's state (§5.1) as a Playwright <see cref="Microsoft.Playwright.LoadState"/>.</summary>
    /// <param name="state">The page load state to await.</param>
    public static LoadState LoadState(string state) => state switch
    {
        "domcontentloaded" => Microsoft.Playwright.LoadState.DOMContentLoaded,
        "networkidle" => Microsoft.Playwright.LoadState.NetworkIdle,
        _ => Microsoft.Playwright.LoadState.Load,
    };

    /// <summary>The <c>waitFor</c> node's element state (§5.1) as a Playwright <see cref="WaitForSelectorState"/>.</summary>
    /// <param name="state">The element state to await.</param>
    public static WaitForSelectorState WaitForState(string state) => state switch
    {
        "hidden" => WaitForSelectorState.Hidden,
        "attached" => WaitForSelectorState.Attached,
        "detached" => WaitForSelectorState.Detached,
        _ => WaitForSelectorState.Visible,
    };
}
