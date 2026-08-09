using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.XPath;
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

    /// <summary>A page-scoped root locator (<c>page.Locator(selector)</c>), re-queried against the current document. The
    /// selector is CSS by default; a <c>"xpath=…"</c> prefix (§5.2, Playwright-style) roots an XPath query instead.</summary>
    /// <param name="page">The owning page (source of the current document).</param>
    /// <param name="selector">The CSS selector, or <c>"xpath=…"</c> for XPath.</param>
    internal static FakeLocatorHandle Css(FakePageHandle page, string selector) =>
        new(page, null, () => RootQuery(page.CurrentDocument, selector));

    /// <summary>A page-scoped title locator (<c>page.GetByTitle(title)</c>), modelled as <c>[title='…']</c>.</summary>
    /// <param name="page">The owning page.</param>
    /// <param name="title">The title text to match.</param>
    internal static FakeLocatorHandle Title(FakePageHandle page, string title) =>
        new(page, null, () => Query(page.CurrentDocument, $"[title=\"{title}\"]"));

    /// <summary>A page-scoped role locator (<c>page.GetByRole(role, name)</c>, §5.2): the role's implicit-element set,
    /// optionally narrowed to elements whose accessible name contains <paramref name="name"/> (case-insensitive,
    /// whitespace-normalised substring — Playwright's default). Models Playwright's ARIA-tree matching over the flat DOM:
    /// each role maps to the HTML elements that carry it implicitly plus an explicit <c>[role=…]</c>, and the accessible
    /// name is the element's <c>aria-label</c> else its text content (the common sources; the reference uses no role
    /// selectors, so richer accname sources — an input button's value, an image's alt — are a noted fake approximation).</summary>
    /// <param name="page">The owning page.</param>
    /// <param name="role">The ARIA role.</param>
    /// <param name="name">The accessible-name substring to require, or null for every element of the role.</param>
    internal static FakeLocatorHandle Role(FakePageHandle page, string role, string? name) =>
        new(page, null, () => RoleQuery(page.CurrentDocument, role, name));

    /// <summary>A page-scoped text locator (<c>page.GetByText(text)</c>, §5.2): the innermost elements whose text content
    /// contains <paramref name="text"/> (case-insensitive, whitespace-normalised substring — Playwright's default). An
    /// element matches only when no child element also carries the text, so the deepest node wins (Playwright's
    /// "smallest element" rule).</summary>
    /// <param name="page">The owning page.</param>
    /// <param name="text">The text substring to match.</param>
    internal static FakeLocatorHandle Text(FakePageHandle page, string text) =>
        new(page, null, () => TextQuery(page.CurrentDocument, text));

    /// <summary>A frame-scoped root locator (<c>frameLocator.Locator(selector)</c>), re-queried against the frame's
    /// current document. CSS by default, or <c>"xpath=…"</c> for XPath (§5.2). The frame tag rides along so a click on
    /// this handle (or any child of it) matches an in-frame transition, not a page-level one (§ frames).</summary>
    /// <param name="page">The owning page (source of the frame document).</param>
    /// <param name="frameSelector">The iframe element's CSS selector — which frame's document to query and click inside.</param>
    /// <param name="selector">The selector, evaluated against the frame's current document (CSS or <c>"xpath=…"</c>).</param>
    internal static FakeLocatorHandle InFrame(FakePageHandle page, string frameSelector, string selector) =>
        new(page, frameSelector, () => RootQuery(page.FrameDocument(frameSelector), selector));

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

    // Roots a selector at a document (page or frame): CSS by default, or an XPath query when the "xpath=" engine prefix
    // is present (§5.2). The prefix is Playwright's own — routing structured `{ xpath }` through `Locator("xpath=…")`
    // means the string form ("xpath=…") and the structured form share this one path, on the fake as on the real backend.
    private static List<IElement> RootQuery(IDocument document, string selector) =>
        selector.StartsWith(_xpathPrefix, StringComparison.Ordinal)
            ? XPathQuery(document, selector[_xpathPrefix.Length..])
            : Query(document, selector);

    // Evaluates an XPath expression over the AngleSharp document (AngleSharp.XPath), keeping only element results — an
    // attribute/text-node result is not an actionable locator, matching Playwright's xpath engine which yields elements.
    // Rooted at the document element; a leading "//" anchors at the document root regardless, so it is document-wide,
    // exactly like Playwright's page-level xpath. A parsed document always has a document element (AngleSharp fabricates
    // <html> even for empty input), so the null-forgiving deref never faults.
    private static List<IElement> XPathQuery(IDocument document, string xpath) =>
        [.. document.DocumentElement!.SelectNodes(xpath).OfType<IElement>()];

    // page.GetByRole: the role's element set (implicit HTML elements ∪ explicit [role=…]), optionally name-filtered.
    private static List<IElement> RoleQuery(IParentNode scope, string role, string? name)
    {
        var selector = _roleSelectors.TryGetValue(role, out var css) ? css : $"[role={role}]";
        var matches = Query(scope, selector);
        if (name is null)
        {
            return matches;
        }

        var needle = Normalize(name);
        return [.. matches.Where(e => AccessibleName(e).Contains(needle, StringComparison.OrdinalIgnoreCase))];
    }

    // page.GetByText: elements whose normalised text contains the needle, keeping only the innermost (an element is
    // skipped when a child element already carries the text — text is cumulative, so a matching descendant means a
    // matching direct child, and the deepest node is the one Playwright resolves).
    private static List<IElement> TextQuery(IParentNode scope, string text)
    {
        var needle = Normalize(text);
        var result = new List<IElement>();
        foreach (var element in scope.QuerySelectorAll("*"))
        {
            if (Normalize(element.TextContent).Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !element.Children.Any(c => Normalize(c.TextContent).Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(element);
            }
        }

        return result;
    }

    // The accessible name Playwright's getByRole name option matches against: aria-label when set, else the text content.
    private static string AccessibleName(IElement element)
    {
        var label = element.GetAttribute("aria-label");
        return Normalize(string.IsNullOrEmpty(label) ? element.TextContent : label);
    }

    // Whitespace normalisation shared by role/text matching (collapse runs to one space, trim) — Playwright normalises
    // whitespace on both the element text and the needle before comparing.
    private static string Normalize(string value) => _whitespace.Replace(value, " ").Trim();

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

    private const string _xpathPrefix = "xpath=";

    private static readonly Regex _whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Each ARIA role → the CSS matching the HTML elements that carry it implicitly, unioned with an explicit [role=…].
    // An unlisted role falls back to [role=…] alone (an explicit-role match); a comprehensive implicit-role table is not
    // needed because the acceptance suite uses no role selectors — this covers the common interactive/structural roles.
    private static readonly Dictionary<string, string> _roleSelectors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["button"] = "button, input[type=button], input[type=submit], input[type=reset], [role=button]",
        ["link"] = "a[href], area[href], [role=link]",
        ["heading"] = "h1, h2, h3, h4, h5, h6, [role=heading]",
        ["textbox"] = "input[type=text], input[type=search], input[type=email], input[type=tel], input[type=url], input:not([type]), textarea, [role=textbox]",
        ["listitem"] = "li, [role=listitem]",
        ["checkbox"] = "input[type=checkbox], [role=checkbox]",
    };
}
