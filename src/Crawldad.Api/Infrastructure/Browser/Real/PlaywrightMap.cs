using Crawldad.Api.Features.Runs.Interpreter;
using Microsoft.Playwright;

namespace Crawldad.Api.Infrastructure.Browser.Real;

/// <summary>Translates the seam's string/int arguments into the Playwright option enums the wrappers pass through,
/// kept in one place so the mapping is uniform and unit-testable without a browser. A null timeout maps to
/// Playwright's context default.</summary>
internal static class PlaywrightMap
{
    /// <summary>A per-call timeout override in milliseconds as Playwright's nullable float; null ⇒ the context default.</summary>
    public static float? Timeout(int? ms) => ms.HasValue ? ms.Value : null;

    /// <summary>The <c>goto</c> node's wait-until as a Playwright <see cref="WaitUntilState"/>; null/unknown ⇒ the backend default.</summary>
    public static WaitUntilState? WaitUntil(string? waitUntil) => waitUntil switch
    {
        "load" => WaitUntilState.Load,
        "domcontentloaded" => WaitUntilState.DOMContentLoaded,
        "networkidle" => WaitUntilState.NetworkIdle,
        "commit" => WaitUntilState.Commit,
        _ => null,
    };

    /// <summary>The <c>waitForLoadState</c> node's state as a Playwright <see cref="Microsoft.Playwright.LoadState"/>.</summary>
    public static LoadState LoadState(string state) => state switch
    {
        "domcontentloaded" => Microsoft.Playwright.LoadState.DOMContentLoaded,
        "networkidle" => Microsoft.Playwright.LoadState.NetworkIdle,
        _ => Microsoft.Playwright.LoadState.Load,
    };

    /// <summary>A structured <c>Sel</c>'s <c>role</c> as a Playwright <see cref="AriaRole"/>, matched case-insensitively.
    /// An unrecognised role throws a terminal <c>malformed_node</c> <see cref="InterpreterException"/> — a payload
    /// authoring error, not a silent no-match.</summary>
    public static AriaRole Role(string role) =>
        char.IsLetter(role.FirstOrDefault()) && Enum.TryParse<AriaRole>(role, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InterpreterException(InterpreterErrorCodes.MalformedNode, $"unknown ARIA role '{role}'");

    /// <summary>The <c>waitFor</c> node's element state as a Playwright <see cref="WaitForSelectorState"/>.</summary>
    public static WaitForSelectorState WaitForState(string state) => state switch
    {
        "hidden" => WaitForSelectorState.Hidden,
        "attached" => WaitForSelectorState.Attached,
        "detached" => WaitForSelectorState.Detached,
        _ => WaitForSelectorState.Visible,
    };
}
