namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>
/// A read-only child scope introducing one binding for a binding builtin's per-element body — the <c>v</c> in
/// <c>filter(list, v, pred)</c> / <c>map</c> / <c>any</c> / <c>all</c> / <c>sortBy</c> (§7.2). The binding shadows any
/// outer name of the same identifier for the body only, exactly like a loop variable (§8.2); every other lookup —
/// other variables, <c>pageUrl()</c>, and DOM access — delegates to the <paramref name="parent"/>. Predicates and
/// map-bodies therefore read the live DOM (content-aware conditions are legal), and nested binding builtins compose
/// because each layer wraps the current scope.
///
/// <para>Immutable and allocation-cheap: a fresh instance per element, never mutating shared state, so the
/// <see cref="IEvalScope"/> read-only invariant holds by construction (unlike a mutate-and-restore shadow). The
/// interpreter's <c>for</c>/<c>forEach</c> loop variables could adopt this same decorator in place of
/// <c>RunScope.Shadow</c> if a non-mutating loop scope is ever preferred.</para>
/// </summary>
/// <param name="parent">The scope this binding layers over.</param>
/// <param name="name">The bound identifier (introduced by the binding builtin's binding slot).</param>
/// <param name="value">The current element bound to <paramref name="name"/> for one body evaluation.</param>
internal sealed class BindingScope(IEvalScope parent, string name, object? value) : IEvalScope
{
    public bool TryResolve(string identifier, out object? resolved)
    {
        if (string.Equals(identifier, name, StringComparison.Ordinal))
        {
            resolved = value;
            return true;
        }

        return parent.TryResolve(identifier, out resolved);
    }

    public string PageUrl() => parent.PageUrl();

    public IDomAccess Dom => parent.Dom;
}
