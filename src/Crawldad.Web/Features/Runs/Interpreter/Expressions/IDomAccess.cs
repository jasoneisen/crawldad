namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>
/// The only page access an expression has (§7): read-only DOM queries. The interpreter work package implements
/// this over Playwright locators; unit tests fake it. Every method is async because real backends are async
/// (a remote CDP round-trip). The <paramref name="target"/> on each call is one of the three shapes the DOM
/// builtins accept (§7.2): a selector <see cref="string"/>, an opaque locator handle, or a structured
/// <c>Sel</c> map (<see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/> of <see cref="string"/> to
/// <see cref="object"/>) passed through untouched — the interpreter resolves it. The optional
/// <c>relativeCss</c> narrows to a child of the target (the <c>text(base, "css")</c> relative form).
/// </summary>
public interface IDomAccess
{
    /// <summary>Counts elements the target matches (<c>CountAsync</c>), optionally narrowed by <paramref name="relativeCss"/>.</summary>
    /// <param name="target">Selector string, opaque handle, or structured <c>Sel</c> map.</param>
    /// <param name="relativeCss">A child CSS selector to narrow to, or null for the target itself.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<long> CountAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>Whether at least one element matches (<c>exists</c>), optionally narrowed by <paramref name="relativeCss"/>.</summary>
    /// <param name="target">Selector string, opaque handle, or structured <c>Sel</c> map.</param>
    /// <param name="relativeCss">A child CSS selector to narrow to, or null for the target itself.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<bool> ExistsAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>The first matched element's <c>textContent</c>, or null when no node matches (which then null-propagates through string builtins).</summary>
    /// <param name="target">Selector string, opaque handle, or structured <c>Sel</c> map.</param>
    /// <param name="relativeCss">A child CSS selector to narrow to, or null for the target itself.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<string?> TextAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>The first matched element's rendered <c>innerText</c>, or null when no node matches.</summary>
    /// <param name="target">Selector string, opaque handle, or structured <c>Sel</c> map.</param>
    /// <param name="relativeCss">A child CSS selector to narrow to, or null for the target itself.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<string?> InnerTextAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>The first matched element's <c>innerHTML</c>, or null when no node matches.</summary>
    /// <param name="target">Selector string, opaque handle, or structured <c>Sel</c> map.</param>
    /// <param name="relativeCss">A child CSS selector to narrow to, or null for the target itself.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<string?> InnerHtmlAsync(object target, string? relativeCss, CancellationToken ct);

    /// <summary>The first matched element's <paramref name="name"/> attribute, or null when the node or attribute is absent.</summary>
    /// <param name="target">Selector string, opaque handle, or structured <c>Sel</c> map.</param>
    /// <param name="relativeCss">A child CSS selector to narrow to, or null for the target itself.</param>
    /// <param name="name">The attribute name to read.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<string?> AttrAsync(object target, string? relativeCss, string name, CancellationToken ct);
}
