namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>
/// The §7.2 binding surface — <c>filter</c>/<c>map</c>/<c>any</c>/<c>all</c>/<c>sortBy</c> — the only builtins that
/// introduce a scoped variable. Each takes the fixed <c>fn(source, v, body)</c> form: the source list, a bare binding
/// identifier <c>v</c>, and a body evaluated once per element in a <see cref="BindingScope"/> (so <c>v</c> shadows outer
/// names for the body only, §8.2). Bodies may read the DOM (content-aware predicates are legal) and nest — the §7.4
/// flagship <c>filter(map(keys(parents), k, toInt(k)), n, n &lt; indent)</c> composes because each layer wraps the
/// current scope. Iteration is bounded by the (already-finite) input list, so evaluation always terminates.
///
/// <para>The list argument <b>null-propagates</b> (null → null, §7.1). Empty-list results follow C# LINQ: <c>any</c>
/// → false, <c>all</c> → true (vacuous), <c>filter</c>/<c>map</c>/<c>sortBy</c> → empty. <c>any</c>/<c>all</c>
/// short-circuit.</para>
/// </summary>
internal static partial class BuiltinRegistry
{
    private static IEnumerable<BindingBuiltin> BindingBuiltins() =>
    [
        new BindingBuiltin("filter", FilterAsync),
        new BindingBuiltin("map", MapAsync),
        new BindingBuiltin("any", AnyAsync),
        new BindingBuiltin("all", AllAsync),
        new BindingBuiltin("sortBy", SortByAsync),
    ];

    // filter(list, v, pred) — the elements for which pred (bound to v) is true.
    private static async ValueTask<object?> FilterAsync(ExpressionNode source, string binding, ExpressionNode body, EvalContext ctx)
    {
        var value = await source.EvaluateAsync(ctx);
        if (value is null)
        {
            return null;
        }

        var result = new List<object?>();
        foreach (var item in RequireArray(value, "filter"))
        {
            if (ExpressionValues.RequireBool(await body.EvaluateAsync(Bind(ctx, binding, item))))
            {
                result.Add(item);
            }
        }

        return result;
    }

    // map(list, v, expr) — expr (bound to v) applied to each element.
    private static async ValueTask<object?> MapAsync(ExpressionNode source, string binding, ExpressionNode body, EvalContext ctx)
    {
        var value = await source.EvaluateAsync(ctx);
        if (value is null)
        {
            return null;
        }

        var list = RequireArray(value, "map");
        var result = new List<object?>(list.Count);
        foreach (var item in list)
        {
            result.Add(await body.EvaluateAsync(Bind(ctx, binding, item)));
        }

        return result;
    }

    // any(list, v, pred) — true when pred holds for at least one element; short-circuits on the first true.
    private static async ValueTask<object?> AnyAsync(ExpressionNode source, string binding, ExpressionNode body, EvalContext ctx)
    {
        var value = await source.EvaluateAsync(ctx);
        if (value is null)
        {
            return null;
        }

        foreach (var item in RequireArray(value, "any"))
        {
            if (ExpressionValues.RequireBool(await body.EvaluateAsync(Bind(ctx, binding, item))))
            {
                return true;
            }
        }

        return false;
    }

    // all(list, v, pred) — true when pred holds for every element (vacuously true when empty); short-circuits on false.
    private static async ValueTask<object?> AllAsync(ExpressionNode source, string binding, ExpressionNode body, EvalContext ctx)
    {
        var value = await source.EvaluateAsync(ctx);
        if (value is null)
        {
            return null;
        }

        foreach (var item in RequireArray(value, "all"))
        {
            if (!ExpressionValues.RequireBool(await body.EvaluateAsync(Bind(ctx, binding, item))))
            {
                return false;
            }
        }

        return true;
    }

    // sortBy(list, v, key) — a stable ascending sort by the (bound-to-v) key. Keys must be homogeneous — all numeric or
    // all string — else a terminal type_error; List.OrderBy is stable, so equal keys keep their original order.
    private static async ValueTask<object?> SortByAsync(ExpressionNode source, string binding, ExpressionNode body, EvalContext ctx)
    {
        var value = await source.EvaluateAsync(ctx);
        if (value is null)
        {
            return null;
        }

        var keyed = new List<(object? Item, object? Key)>();
        var hasNumber = false;
        var hasString = false;
        foreach (var item in RequireArray(value, "sortBy"))
        {
            var key = await body.EvaluateAsync(Bind(ctx, binding, item));
            if (ExpressionValues.IsNumber(key))
            {
                hasNumber = true;
            }
            else if (key is string)
            {
                hasString = true;
            }
            else
            {
                throw ExpressionValues.TypeError($"sortBy key must be a number or string, got {ExpressionValues.TypeName(key)}");
            }

            keyed.Add((item, key));
        }

        if (hasNumber && hasString)
        {
            throw ExpressionValues.TypeError("sortBy keys must be all numbers or all strings");
        }

        var sorted = hasString
            ? keyed.OrderBy(k => (string)k.Key!, StringComparer.Ordinal)
            : keyed.OrderBy(k => ExpressionValues.RequireNumber(k.Key, "sortBy"));
        return sorted.Select(k => k.Item).ToList();
    }

    // Wraps the scope in a child binding for one element's body evaluation (never mutating the parent).
    private static EvalContext Bind(EvalContext ctx, string binding, object? value) =>
        ctx with { Scope = new BindingScope(ctx.Scope, binding, value) };
}
