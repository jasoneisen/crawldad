using AngleSharp.Dom;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>
/// A <b>lazy</b> AngleSharp-backed locator (§ seam contract). It captures a <em>query</em> — a resolver closure over
/// the page's <em>current</em> document — never a node snapshot, so every terminal re-evaluates against the DOM as it
/// stands at call time. Refinements (<see cref="Locator"/>/<see cref="Nth"/>/<see cref="First"/>/<see cref="Filter"/>)
/// return a new handle narrowing this one and touch no DOM themselves. This reproduces Playwright's re-query-on-use
/// semantics the reference relies on after a grid postback (a row handle bound before the swap resolves to the fresh
/// rows after it).
/// </summary>
internal sealed class FakeLocatorHandle : ILocatorHandle
{
    private readonly FakePageHandle _page;
    private readonly string? _frame;
    private readonly Func<IReadOnlyList<IElement>> _resolve;

    private FakeLocatorHandle(FakePageHandle page, string? frame, Func<IReadOnlyList<IElement>> resolve)
    {
        _page = page;
        _frame = frame;
        _resolve = resolve;
    }

    /// <summary>A page-scoped CSS root locator (<c>page.Locator(css)</c>), re-queried against the current document.</summary>
    /// <param name="page">The owning page (source of the current document).</param>
    /// <param name="selector">The CSS selector.</param>
    internal static FakeLocatorHandle Css(FakePageHandle page, string selector) =>
        new(page, null, () => Query(page.CurrentDocument, selector));

    /// <summary>A page-scoped title locator (<c>page.GetByTitle(title)</c>), modelled as <c>[title='…']</c>.</summary>
    /// <param name="page">The owning page.</param>
    /// <param name="title">The title text to match.</param>
    internal static FakeLocatorHandle Title(FakePageHandle page, string title) =>
        new(page, null, () => Query(page.CurrentDocument, $"[title=\"{title}\"]"));

    /// <summary>A frame-scoped CSS root locator (<c>frameLocator.Locator(css)</c>), re-queried against the frame's
    /// current document. The frame tag rides along so a click on this handle (or any child of it) matches an in-frame
    /// transition, not a page-level one (§ frames).</summary>
    /// <param name="page">The owning page (source of the frame document).</param>
    /// <param name="frameSelector">The iframe element's CSS selector — which frame's document to query and click inside.</param>
    /// <param name="selector">The CSS selector, evaluated against the frame's current document.</param>
    internal static FakeLocatorHandle InFrame(FakePageHandle page, string frameSelector, string selector) =>
        new(page, frameSelector, () => Query(page.FrameDocument(frameSelector), selector));

    public ILocatorHandle Locator(string childSelector) =>
        new FakeLocatorHandle(_page, _frame, () => ChildQuery(_resolve(), childSelector));

    public ILocatorHandle Nth(int index) =>
        new FakeLocatorHandle(_page, _frame, () =>
        {
            var matches = _resolve();
            return index >= 0 && index < matches.Count ? [matches[index]] : [];
        });

    public ILocatorHandle First => Nth(0);

    public ILocatorHandle Filter(string hasTextRegex)
    {
        // Size- and time-guarded through the shared GuardedRegex factory (§7.2), same as the matches/replaceRegex
        // builtins; built once here and captured by the resolver.
        var regex = GuardedRegex.Compile(hasTextRegex);
        return new FakeLocatorHandle(_page, _frame, () => _resolve().Where(e => regex.IsMatch(e.TextContent)).ToList());
    }

    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(_resolve().Count);

    public Task<string?> TextContentAsync(CancellationToken ct) => Task.FromResult(First0()?.TextContent);

    // AngleSharp does no layout, so it has no rendered innerText. FakeInnerText approximates Chromium's rendering
    // (<br>/block boundaries → newline, inline whitespace collapse) so the processing-status region's
    // split(innerText(...), '\n') dissects the lines a browser would produce; re-gated against real Chromium in P4.
    public Task<string> InnerTextAsync(CancellationToken ct)
    {
        var element = First0();
        return Task.FromResult(element is null ? string.Empty : FakeInnerText.Render(element));
    }

    public Task<string> InnerHTMLAsync(CancellationToken ct) => Task.FromResult(First0()?.InnerHtml ?? string.Empty);

    public Task<string?> GetAttributeAsync(string name, CancellationToken ct) => Task.FromResult(First0()?.GetAttribute(name));

    public Task ClickAsync(int? timeoutMs, CancellationToken ct)
    {
        _page.HandleClick(First0(), _frame);
        return Task.CompletedTask;
    }

    public Task FillAsync(string value, CancellationToken ct)
    {
        First0()?.SetAttribute("value", value);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        First0()?.SetAttribute("value", string.Empty);
        return Task.CompletedTask;
    }

    public Task WaitForAsync(string state, int? timeoutMs, CancellationToken ct)
    {
        // P1: only "hidden" is checked (the reference's #divGlobalLoading overlay); other states are no-op successes.
        // "hidden" succeeds when the element is absent or carries display:none; a still-visible element is a timeout.
        if (string.Equals(state, "hidden", StringComparison.Ordinal) && First0() is { } el && !IsHidden(el))
        {
            throw new BrowserTimeoutException("waited for an element to become hidden but it is visible");
        }

        return Task.CompletedTask;
    }

    private IElement? First0()
    {
        var matches = _resolve();
        return matches.Count > 0 ? matches[0] : null;
    }

    private static bool IsHidden(IElement el)
    {
        var style = el.GetAttribute("style");
        return style is not null
            && style.Replace(" ", string.Empty, StringComparison.Ordinal).Contains("display:none", StringComparison.Ordinal);
    }

    private static List<IElement> Query(IParentNode scope, string selector) => [.. scope.QuerySelectorAll(selector)];

    private static List<IElement> ChildQuery(IReadOnlyList<IElement> parents, string selector)
    {
        var scoped = ScopeRelative(selector);
        var seen = new HashSet<IElement>();
        var result = new List<IElement>();
        foreach (var parent in parents)
        {
            foreach (var element in parent.QuerySelectorAll(scoped))
            {
                if (seen.Add(element))
                {
                    result.Add(element);
                }
            }
        }

        return result;
    }

    // Playwright's chained Locator (`parent.Locator(css)`) scopes to the parent's STRICT DESCENDANTS: the selector's
    // leftmost compound must match a descendant, never the parent element itself or an ancestor. AngleSharp's
    // element.QuerySelectorAll follows the DOM spec, where the leftmost compound is matched against the whole document
    // (only the RESULT must be a descendant) — the "querySelectorAll ancestor-leakage" gotcha. That diverges from
    // Chromium whenever the leftmost type can match the parent: the owner block's `table tr:first-child td` (parent is
    // itself a <table>) leaks `table` onto the parent and reads the parent's own wrapper cell (the whole block) instead
    // of the inner name cell; likewise `table tr` counts the parent's own row. Anchoring every relative selector with a
    // leading ':scope ' forces descendant-only matching — reproducing Playwright's chained-locator semantics — and also
    // subsumes the leading child combinator the related-records region uses (`> td:nth-child(2)` → `:scope > td:…`,
    // a DIRECT child read). Verified against Playwright's behaviour; re-gated against real Chromium in Phase 4.
    private static string ScopeRelative(string selector) => ":scope " + selector.TrimStart();
}
