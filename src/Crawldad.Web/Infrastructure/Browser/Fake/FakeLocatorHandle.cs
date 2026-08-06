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
    private readonly Func<IReadOnlyList<IElement>> _resolve;

    private FakeLocatorHandle(FakePageHandle page, Func<IReadOnlyList<IElement>> resolve)
    {
        _page = page;
        _resolve = resolve;
    }

    /// <summary>A page-scoped CSS root locator (<c>page.Locator(css)</c>), re-queried against the current document.</summary>
    /// <param name="page">The owning page (source of the current document).</param>
    /// <param name="selector">The CSS selector.</param>
    internal static FakeLocatorHandle Css(FakePageHandle page, string selector) =>
        new(page, () => Query(page.CurrentDocument, selector));

    /// <summary>A page-scoped title locator (<c>page.GetByTitle(title)</c>), modelled as <c>[title='…']</c>.</summary>
    /// <param name="page">The owning page.</param>
    /// <param name="title">The title text to match.</param>
    internal static FakeLocatorHandle Title(FakePageHandle page, string title) =>
        new(page, () => Query(page.CurrentDocument, $"[title=\"{title}\"]"));

    public ILocatorHandle Locator(string childSelector) =>
        new FakeLocatorHandle(_page, () => ChildQuery(_resolve(), childSelector));

    public ILocatorHandle Nth(int index) =>
        new FakeLocatorHandle(_page, () =>
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
        return new FakeLocatorHandle(_page, () => _resolve().Where(e => regex.IsMatch(e.TextContent)).ToList());
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
        _page.HandleClick(First0());
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

    // Playwright scopes a relative selector that begins with a child combinator ('>') to the locator's own element
    // (e.g. the related-records `> td:nth-child(2)` reads a DIRECT child cell). AngleSharp's element.QuerySelectorAll
    // ignores a leading '>' and scans descendants instead — so `> td:nth-child(2)` would match a deeply nested cell,
    // diverging from Chromium. Anchor it with ':scope' to recover Playwright's semantics. Selectors without a leading
    // combinator are already a descendant match in both engines and pass through unchanged. Only '>' is handled — the
    // one leading combinator the reference uses (:662-665).
    private static string ScopeRelative(string selector)
    {
        var trimmed = selector.TrimStart();
        return trimmed.StartsWith('>') ? ":scope " + trimmed : selector;
    }
}
