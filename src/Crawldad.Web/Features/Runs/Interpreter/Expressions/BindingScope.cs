namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>A read-only child scope binding one identifier (the <c>v</c> in <c>filter</c>/<c>map</c>/<c>any</c>/
/// <c>all</c>/<c>sortBy</c>) for a single per-element body evaluation; other lookups delegate to
/// <paramref name="parent"/>. Immutable per instance, so nested binding builtins compose safely.</summary>
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

    // A binding body's extraction misses report to the same run sink as the enclosing expression.
    public ISelectorMissSink Misses => parent.Misses;
}
