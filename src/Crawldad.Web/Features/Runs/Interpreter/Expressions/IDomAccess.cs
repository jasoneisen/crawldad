namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>The only page access an expression has: read-only DOM queries, implemented over Playwright locators (faked
/// in tests). <paramref name="target"/> on each call is a selector string, an opaque locator handle, or a structured
/// <c>Sel</c> map passed through untouched; the optional <c>relativeCss</c> narrows to a child of the target.</summary>
public interface IDomAccess
{
    /// <summary>Counts elements the target matches (<c>CountAsync</c>), optionally narrowed by <paramref name="relativeCss"/>.</summary>
    ValueTask<long> CountAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>Whether at least one element matches (<c>exists</c>), optionally narrowed by <paramref name="relativeCss"/>.</summary>
    ValueTask<bool> ExistsAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>The first matched element's <c>textContent</c>, or null when no node matches (which then null-propagates through string builtins).</summary>
    ValueTask<string?> TextAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>The first matched element's rendered <c>innerText</c>, or null when no node matches.</summary>
    ValueTask<string?> InnerTextAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>The first matched element's <c>innerHTML</c>, or null when no node matches.</summary>
    ValueTask<string?> InnerHtmlAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>The first matched element's <paramref name="name"/> attribute, or null when the node or attribute is absent.</summary>
    ValueTask<string?> AttrAsync(object target, string? relativeCss, string name, CancellationToken ct);
}
