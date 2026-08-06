using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>
/// The evaluation environment an expression reads from (§8.2 flat run scope). The interpreter work package
/// implements this over the run's flat variable scope — <c>input.*</c>, declared <c>vars</c>, and loop
/// variables — plus the live page. Expressions never mutate it: all state mutation is structural (§6), so this
/// seam is read-only by construction.
/// </summary>
public interface IEvalScope
{
    /// <summary>
    /// Resolves a bare identifier (<c>input</c>, a declared var, a loop var) to its current value. Returns
    /// <see langword="false"/> when the name is unbound — the evaluator turns that into a terminal
    /// <c>unknown_identifier</c> failure, since the parser cannot know run-time variable names.
    /// </summary>
    /// <param name="name">The identifier to resolve.</param>
    /// <param name="value">The bound value (any value-model type or an opaque handle) when found.</param>
    /// <returns><see langword="true"/> if the name is bound in scope; otherwise <see langword="false"/>.</returns>
    bool TryResolve(string name, out object? value);

    /// <summary>The current page URL, backing the <c>pageUrl()</c> builtin.</summary>
    /// <returns>The absolute URL string of the page the run is currently on.</returns>
    [SuppressMessage("Design", "CA1055:URI-like return values should not be strings",
        Justification = "The value flows straight into the value model as a string and into System.Uri parsing in the URL builtins; the browser seam models URLs as strings for the same reason.")]
    string PageUrl();

    /// <summary>The read-only DOM access seam the page-querying builtins (<c>count/exists/text/…</c>) go through.</summary>
    IDomAccess Dom { get; }
}
