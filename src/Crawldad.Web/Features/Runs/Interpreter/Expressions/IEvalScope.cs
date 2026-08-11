using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>The evaluation environment an expression reads from: the run's flat variable scope (<c>input.*</c>,
/// declared <c>vars</c>, loop variables) plus the live page. Expressions never mutate it — all state mutation is
/// structural, so this seam is read-only by construction.</summary>
public interface IEvalScope
{
    /// <summary>Resolves a bare identifier (<c>input</c>, a declared var, a loop var) to its current value. Returns
    /// false when unbound — the evaluator turns that into a terminal <c>unknown_identifier</c> failure, since the
    /// parser cannot know run-time variable names.</summary>
    bool TryResolve(string name, out object? value);

    /// <summary>The current page URL, backing the <c>pageUrl()</c> builtin.</summary>
    [SuppressMessage("Design", "CA1055:URI-like return values should not be strings",
        Justification = "The value flows straight into the value model as a string and into System.Uri parsing in the URL builtins; the browser seam models URLs as strings for the same reason.")]
    string PageUrl();

    /// <summary>The read-only DOM access seam the page-querying builtins (<c>count/exists/text/…</c>) go through.</summary>
    IDomAccess Dom { get; }

    /// <summary>Where the extraction builtins report a selector miss (a target that matched no element). The run scope
    /// backs it with the interpreter's <c>selectorMisses</c> counter and trace stream (making misses countable and
    /// classifiable); a binding scope delegates to its parent; a scope with no run behind it supplies the inert
    /// <see cref="NoSelectorMissSink"/>, which still honours <c>require(...)</c>.</summary>
    ISelectorMissSink Misses { get; }

    /// <summary>The per-evaluation expression step budget: the maximum node evaluations a single
    /// <see cref="CrawldadExpression.EvaluateAsync"/> may spend before it aborts. Carried on the scope so it rides to
    /// every nested evaluation without threading through call sites; a payload can never raise it.</summary>
    int ExpressionStepBudget => CrawldadExpression.DefaultStepBudget;
}
