namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>The collection surface over the array/map value model. Every builtin null-propagates on its primary
/// argument; out-of-range access (empty <c>first</c>/<c>last</c>/<c>min</c>/<c>max</c>, bad <c>nth</c>/<c>slice</c>) is a
/// terminal <c>index_out_of_range</c>, never a silent null. Results are always fresh lists.</summary>
internal static partial class BuiltinRegistry
{
    private static IEnumerable<Builtin> CollectionBuiltins() =>
    [
        Fn1("first", First),
        Fn1("last", Last),
        Fn2("nth", Nth),
        new Builtin("slice", 2, 3, SliceAsync),
        Fn1("reverse", Reverse),
        Fn1("distinct", Distinct),
        Fn1("min", value => Extreme(value, "min", wantGreater: false)),
        Fn1("max", value => Extreme(value, "max", wantGreater: true)),
        Fn1("keys", Keys),
        Fn2("get", Get),
    ];

    // first(x) / last(x) — array head/tail; an empty array is a terminal index_out_of_range (C# .First()/.Last() throw).
    private static object? First(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var list = RequireArray(value, "first");
        return list.Count > 0 ? list[0] : throw IndexError("first of an empty array");
    }

    private static object? Last(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var list = RequireArray(value, "last");
        return list.Count > 0 ? list[^1] : throw IndexError("last of an empty array");
    }

    // nth(x, i) — 0-based element access (Playwright .Nth(i)); out of range is a terminal index_out_of_range.
    private static object? Nth(object? value, object? index)
    {
        if (value is null)
        {
            return null;
        }

        var list = RequireArray(value, "nth");
        var i = ExpressionValues.RequireIndex(index, "nth index");
        return i >= 0 && i < list.Count
            ? list[(int)i]
            : throw IndexError($"nth index {i} is out of range for an array of length {list.Count}");
    }

    // slice(x, a, b?) — (start, endExclusive), mirroring substring on arrays; out-of-range start/end is a terminal
    // index_out_of_range (C# Range semantics — not clamped), not a silent clamp. The 2-arg form runs to the end.
    private static async ValueTask<object?> SliceAsync(IReadOnlyList<ExpressionNode> args, EvalContext ctx)
    {
        var value = await args[0].EvaluateAsync(ctx);
        if (value is null)
        {
            return null;
        }

        var list = RequireArray(value, "slice");
        var start = ExpressionValues.RequireIndex(await args[1].EvaluateAsync(ctx), "slice start");
        var end = args.Count > 2
            ? ExpressionValues.RequireIndex(await args[2].EvaluateAsync(ctx), "slice end")
            : list.Count;

        return start < 0 || end < start || end > list.Count
            ? throw IndexError($"slice range [{start}, {end}) is out of range for an array of length {list.Count}")
            : list.GetRange((int)start, (int)(end - start));
    }

    // reverse(x) — a new list, input untouched.
    private static object? Reverse(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var copy = new List<object?>(RequireArray(value, "reverse"));
        copy.Reverse();
        return copy;
    }

    // distinct(x) — first-occurrence-preserving dedup by scalar value-equality (ordinal strings); a non-scalar
    // element is a terminal type_error (as == rejects it).
    private static object? Distinct(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var seen = new HashSet<object?>(ExpressionValues.ScalarEqualityComparer);
        var result = new List<object?>();
        foreach (var item in RequireArray(value, "distinct"))
        {
            if (item is not (null or bool or long or double or string))
            {
                throw ExpressionValues.TypeError($"distinct expects scalar elements, got {ExpressionValues.TypeName(item)}");
            }

            if (seen.Add(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    // min(x) / max(x) — over a numeric array; returns the extreme element (its own int/double type preserved). An empty
    // array is a terminal index_out_of_range (C# LINQ .Min()/.Max() throw "sequence contains no elements"); a
    // non-numeric element is a terminal type_error.
    private static object? Extreme(object? value, string name, bool wantGreater)
    {
        if (value is null)
        {
            return null;
        }

        var list = RequireArray(value, name);
        if (list.Count == 0)
        {
            throw IndexError($"{name} of an empty array");
        }

        var best = list[0];
        var bestNumber = ExpressionValues.RequireNumber(best, name);
        for (var i = 1; i < list.Count; i++)
        {
            var number = ExpressionValues.RequireNumber(list[i], name);
            var cmp = number.CompareTo(bestNumber);
            if (wantGreater ? cmp > 0 : cmp < 0)
            {
                best = list[i];
                bestNumber = number;
            }
        }

        return best;
    }

    // keys(map) — the map's keys in insertion order.
    private static object? Keys(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value is Dictionary<string, object?> map
            ? new List<object?>(map.Keys)
            : throw ExpressionValues.TypeError($"keys expects a map, got {ExpressionValues.TypeName(value)}");
    }

    // get(map, key) — the value for a string key, or null when absent. Null map propagates to null.
    private static object? Get(object? value, object? key)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not Dictionary<string, object?> map)
        {
            throw ExpressionValues.TypeError($"get expects a map, got {ExpressionValues.TypeName(value)}");
        }

        return key is string k
            ? map.GetValueOrDefault(k)
            : throw ExpressionValues.TypeError($"get key must be a string, got {ExpressionValues.TypeName(key)}");
    }

    // Shared by the collection and binding surfaces.
    private static List<object?> RequireArray(object? value, string role) =>
        value is List<object?> list
            ? list
            : throw ExpressionValues.TypeError($"{role} expects an array, got {ExpressionValues.TypeName(value)}");

    private static ExpressionEvaluationException IndexError(string message) =>
        new(ExpressionErrorCodes.IndexOutOfRange, message);
}
