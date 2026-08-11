namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>A handle to DOM elements matched by a query (a Playwright <c>ILocator</c>). LAZY — load-bearing:
/// captures the query, not a snapshot, and every terminal call re-evaluates it against the current DOM, so a handle
/// bound before a DOM change still resolves fresh elements on next use. Implementations MUST preserve this.</summary>
public interface ILocatorHandle
{
    /// <summary>Narrows to a child locator relative to this one (<c>locator.Locator</c>), lazily.</summary>
    /// <param name="childSelector">A CSS selector evaluated relative to each element this handle matches.</param>
    ILocatorHandle Locator(string childSelector);

    /// <summary>Narrows to the element at <paramref name="index"/> (<c>locator.Nth</c>, zero-based), lazily.</summary>
    /// <param name="index">Zero-based index into the matched set.</param>
    ILocatorHandle Nth(int index);

    /// <summary>Narrows to the first matched element (<c>locator.First</c>), lazily.</summary>
    ILocatorHandle First { get; }

    /// <summary>Narrows to elements whose text matches <paramref name="hasTextRegex"/> (<c>locator.Filter(HasTextRegex)</c>), lazily.</summary>
    /// <param name="hasTextRegex">A regular expression the element's text must match.</param>
    ILocatorHandle Filter(string hasTextRegex);

    /// <summary>Counts the elements the query currently matches (<c>CountAsync</c>). Re-evaluates the DOM.</summary>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>The first matched element's <c>textContent</c> (<c>TextContentAsync</c>), or null if absent. Re-evaluates the DOM.</summary>
    Task<string?> TextContentAsync(CancellationToken ct);

    /// <summary>The first matched element's rendered <c>innerText</c> (<c>InnerTextAsync</c>). Re-evaluates the DOM.</summary>
    Task<string> InnerTextAsync(CancellationToken ct);

    /// <summary>The first matched element's <c>innerHTML</c> (<c>InnerHTMLAsync</c>). Re-evaluates the DOM.</summary>
    Task<string> InnerHTMLAsync(CancellationToken ct);

    /// <summary>The first matched element's <c>outerHTML</c> — the element itself plus its subtree — for a
    /// <c>capture</c> node targeting an element (its serialised subtree, not just its children). Empty when no node
    /// matches, matching <see cref="InnerHTMLAsync"/>'s zero-match short-circuit. Re-evaluates the DOM.</summary>
    Task<string> OuterHTMLAsync(CancellationToken ct);

    /// <summary>The first matched element's attribute value (<c>GetAttributeAsync</c>), or null if the attribute is absent. Re-evaluates the DOM.</summary>
    Task<string?> GetAttributeAsync(string name, CancellationToken ct);

    /// <summary>Clicks the matched element (<c>ClickAsync</c>). Re-evaluates the DOM.</summary>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the default.</param>
    Task ClickAsync(int? timeoutMs, CancellationToken ct);

    /// <summary>Fills the matched input with <paramref name="value"/> (<c>FillAsync</c>). Re-evaluates the DOM.</summary>
    Task FillAsync(string value, CancellationToken ct);

    /// <summary>Clears the matched input (<c>ClearAsync</c>). Re-evaluates the DOM.</summary>
    Task ClearAsync(CancellationToken ct);

    /// <summary>Waits until the matched element reaches <paramref name="state"/> (<c>WaitForAsync</c>). Re-evaluates the DOM.</summary>
    /// <param name="state">The element state to await (<c>visible</c>/<c>hidden</c>/<c>attached</c>/<c>detached</c>).</param>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the default.</param>
    Task WaitForAsync(string state, int? timeoutMs, CancellationToken ct);
}
