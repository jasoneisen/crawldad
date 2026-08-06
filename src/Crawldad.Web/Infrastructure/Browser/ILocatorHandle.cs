namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>
/// A handle to a set of DOM elements matched by a query (a Playwright <c>ILocator</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Locator handles are LAZY — this is load-bearing.</b> A handle captures its <em>query</em> (a selector, a
/// refinement like <see cref="Nth"/>/<see cref="First"/>/<see cref="Filter"/>, or a child <see cref="Locator"/>
/// chain), not a snapshot of the matched elements. Every terminal operation — <see cref="CountAsync"/>,
/// <see cref="TextContentAsync"/>, <see cref="ClickAsync"/>, … — <b>re-evaluates the query against the current
/// DOM at call time</b>. Two reads of the same handle across a DOM change legitimately return different results.
/// </para>
/// <para>
/// This is exactly Playwright's semantics, and the reference scraper relies on it: after a grid posts back and
/// re-renders, a row handle bound before the postback still resolves to the freshly rendered rows on next use —
/// no rebind needed. Implementations (real adapters and the fake) MUST preserve it: capture the query, resolve on
/// use. Refinement methods (<see cref="Locator"/>, <see cref="Nth"/>, <see cref="First"/>, <see cref="Filter"/>)
/// return a NEW lazy handle narrowing this one; they never touch the DOM themselves.
/// </para>
/// </remarks>
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
    /// <param name="ct">Cancels the read.</param>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>The first matched element's <c>textContent</c> (<c>TextContentAsync</c>), or null if absent. Re-evaluates the DOM.</summary>
    /// <param name="ct">Cancels the read.</param>
    Task<string?> TextContentAsync(CancellationToken ct);

    /// <summary>The first matched element's rendered <c>innerText</c> (<c>InnerTextAsync</c>). Re-evaluates the DOM.</summary>
    /// <param name="ct">Cancels the read.</param>
    Task<string> InnerTextAsync(CancellationToken ct);

    /// <summary>The first matched element's <c>innerHTML</c> (<c>InnerHTMLAsync</c>). Re-evaluates the DOM.</summary>
    /// <param name="ct">Cancels the read.</param>
    Task<string> InnerHTMLAsync(CancellationToken ct);

    /// <summary>The first matched element's attribute value (<c>GetAttributeAsync</c>), or null if the attribute is absent. Re-evaluates the DOM.</summary>
    /// <param name="name">The attribute name to read.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<string?> GetAttributeAsync(string name, CancellationToken ct);

    /// <summary>Clicks the matched element (<c>ClickAsync</c>). Re-evaluates the DOM.</summary>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the default.</param>
    /// <param name="ct">Cancels the click.</param>
    Task ClickAsync(int? timeoutMs, CancellationToken ct);

    /// <summary>Fills the matched input with <paramref name="value"/> (<c>FillAsync</c>). Re-evaluates the DOM.</summary>
    /// <param name="value">The text to fill.</param>
    /// <param name="ct">Cancels the fill.</param>
    Task FillAsync(string value, CancellationToken ct);

    /// <summary>Clears the matched input (<c>ClearAsync</c>). Re-evaluates the DOM.</summary>
    /// <param name="ct">Cancels the clear.</param>
    Task ClearAsync(CancellationToken ct);

    /// <summary>Waits until the matched element reaches <paramref name="state"/> (<c>WaitForAsync</c>). Re-evaluates the DOM.</summary>
    /// <param name="state">The element state to await (<c>visible</c>/<c>hidden</c>/<c>attached</c>/<c>detached</c>).</param>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the default.</param>
    /// <param name="ct">Cancels the wait.</param>
    Task WaitForAsync(string state, int? timeoutMs, CancellationToken ct);
}
