namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>The environment threaded through evaluation: the read-only <see cref="IEvalScope"/> and the run's
/// cancellation token. A small struct so every node's <c>EvaluateAsync</c> takes one argument.</summary>
/// <param name="Scope">The flat run scope reads resolve against.</param>
/// <param name="Ct">Cancels an in-flight DOM read.</param>
internal readonly record struct EvalContext(IEvalScope Scope, CancellationToken Ct);

/// <summary>
/// One node of a parsed expression tree (§7.1). Evaluation is async because leaf DOM reads are; pure/arithmetic
/// nodes complete synchronously. A node captures no scope — the same parsed tree is safely reusable across runs.
/// </summary>
internal abstract class ExpressionNode
{
    /// <summary>Evaluates this node against <paramref name="ctx"/>, producing a value-model value or a terminal failure.</summary>
    /// <param name="ctx">The scope + cancellation token.</param>
    public abstract ValueTask<object?> EvaluateAsync(EvalContext ctx);
}

/// <summary>A constant: number (<see cref="long"/>/<see cref="double"/>), string, bool, or null.</summary>
internal sealed class LiteralNode(object? value) : ExpressionNode
{
    public override ValueTask<object?> EvaluateAsync(EvalContext ctx) => new(value);
}

/// <summary>A bare identifier resolved through scope; unbound at eval time is a terminal <c>unknown_identifier</c>.</summary>
internal sealed class IdentifierNode(string name) : ExpressionNode
{
    /// <summary>The identifier text — read by the parser when this node fills a binding builtin's binding slot.</summary>
    public string Name { get; } = name;

    public override ValueTask<object?> EvaluateAsync(EvalContext ctx) =>
        ctx.Scope.TryResolve(Name, out var value)
            ? new ValueTask<object?>(value)
            : throw new ExpressionEvaluationException(
                ExpressionErrorCodes.UnknownIdentifier, $"unknown identifier '{Name}'");
}

/// <summary>An array literal <c>[…]</c> → a fresh <see cref="List{T}"/> of the evaluated elements.</summary>
internal sealed class ArrayNode(IReadOnlyList<ExpressionNode> items) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var list = new List<object?>(items.Count);
        foreach (var item in items)
        {
            list.Add(await item.EvaluateAsync(ctx));
        }

        return list;
    }
}

/// <summary>An object literal <c>{ k: Expr }</c> → an insertion-ordered <see cref="Dictionary{TKey, TValue}"/>.</summary>
internal sealed class ObjectNode(IReadOnlyList<KeyValuePair<string, ExpressionNode>> entries) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, node) in entries)
        {
            map[key] = await node.EvaluateAsync(ctx);
        }

        return map;
    }
}

/// <summary>Logical negation <c>!x</c>: operand must be bool (§7.1).</summary>
internal sealed class NotNode(ExpressionNode operand) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx) =>
        !ExpressionValues.RequireBool(await operand.EvaluateAsync(ctx));
}

/// <summary>Arithmetic negation <c>-x</c>: operand must be a number, else a terminal <c>type_error</c>.</summary>
internal sealed class NegateNode(ExpressionNode operand) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var value = await operand.EvaluateAsync(ctx);
        return value switch
        {
            long l => (object)unchecked(-l),
            double d => -d,
            _ => throw ExpressionValues.TypeError($"unary '-' requires a number, got {ExpressionValues.TypeName(value)}"),
        };
    }
}

/// <summary>An eager binary operator (<c>+ - * / % == != &lt; &lt;= &gt; &gt;=</c>) applied via a value-model delegate.</summary>
internal sealed class BinaryNode(ExpressionNode left, ExpressionNode right, Func<object?, object?, object?> op) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx) =>
        op(await left.EvaluateAsync(ctx), await right.EvaluateAsync(ctx));
}

/// <summary>Short-circuiting <c>&amp;&amp;</c>: both operands must be bool; the right is not evaluated when the left is false.</summary>
internal sealed class AndNode(ExpressionNode left, ExpressionNode right) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        if (!ExpressionValues.RequireBool(await left.EvaluateAsync(ctx)))
        {
            return false;
        }

        return ExpressionValues.RequireBool(await right.EvaluateAsync(ctx));
    }
}

/// <summary>Short-circuiting <c>||</c>: both operands must be bool; the right is not evaluated when the left is true.</summary>
internal sealed class OrNode(ExpressionNode left, ExpressionNode right) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        if (ExpressionValues.RequireBool(await left.EvaluateAsync(ctx)))
        {
            return true;
        }

        return ExpressionValues.RequireBool(await right.EvaluateAsync(ctx));
    }
}

/// <summary>Ternary <c>c ? a : b</c>: the condition must be bool; only the taken branch is evaluated.</summary>
internal sealed class TernaryNode(ExpressionNode condition, ExpressionNode ifTrue, ExpressionNode ifFalse) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx) =>
        ExpressionValues.RequireBool(await condition.EvaluateAsync(ctx))
            ? await ifTrue.EvaluateAsync(ctx)
            : await ifFalse.EvaluateAsync(ctx);
}

/// <summary>Member access <c>a.b</c>: map → value (absent key → null), null → null (models C# <c>?.</c>), else <c>type_error</c>.</summary>
internal sealed class MemberNode(ExpressionNode target, string name) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var value = await target.EvaluateAsync(ctx);
        return value switch
        {
            null => null,
            Dictionary<string, object?> map => map.GetValueOrDefault(name),
            _ => throw ExpressionValues.TypeError($"cannot access member '.{name}' on {ExpressionValues.TypeName(value)}"),
        };
    }
}

/// <summary>
/// Index access <c>a[i]</c>: array with an integer index (out of range, or on null → terminal
/// <c>index_out_of_range</c>, reproducing C# <c>IndexOutOfRangeException</c>); map with a string index → value or
/// null; anything else → <c>type_error</c> (§7.2).
/// </summary>
internal sealed class IndexNode(ExpressionNode target, ExpressionNode index) : ExpressionNode
{
    public override async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var value = await target.EvaluateAsync(ctx);
        var key = await index.EvaluateAsync(ctx);
        switch (value)
        {
            case List<object?> list:
                var i = ToIndex(key);
                if (i < 0 || i >= list.Count)
                {
                    throw new ExpressionEvaluationException(
                        ExpressionErrorCodes.IndexOutOfRange, $"index {i} out of range for array of length {list.Count}");
                }

                return list[(int)i];

            case Dictionary<string, object?> map:
                return map.GetValueOrDefault(RequireStringKey(key));

            case null:
                throw new ExpressionEvaluationException(ExpressionErrorCodes.IndexOutOfRange, "cannot index into null");

            default:
                throw ExpressionValues.TypeError($"cannot index into {ExpressionValues.TypeName(value)}");
        }
    }

    private static long ToIndex(object? key) => key switch
    {
        long l => l,
        double d when d == Math.Floor(d) && !double.IsInfinity(d) => (long)d,
        _ => throw ExpressionValues.TypeError($"array index must be an integer, got {ExpressionValues.TypeName(key)}"),
    };

    private static string RequireStringKey(object? key) =>
        key is string s ? s : throw ExpressionValues.TypeError($"map index must be a string, got {ExpressionValues.TypeName(key)}");
}

/// <summary>A builtin invocation. The builtin is resolved at parse time (unknown/wrong-arity are parse errors), so
/// this node just runs the pre-bound <see cref="BuiltinInvoker"/> over its argument nodes.</summary>
internal sealed class CallNode(BuiltinInvoker invoke, IReadOnlyList<ExpressionNode> args) : ExpressionNode
{
    public override ValueTask<object?> EvaluateAsync(EvalContext ctx) => invoke(args, ctx);
}

/// <summary>
/// A binding builtin invocation (<c>filter</c>/<c>map</c>/<c>any</c>/<c>all</c>/<c>sortBy</c>, §7.2). Distinct from
/// <see cref="CallNode"/> because its middle argument is a <em>binding identifier</em>, not a value: the parser has
/// already validated the <c>(source, binding, body)</c> shape and captured the binding name, so this node runs the
/// pre-bound <see cref="BindingBuiltinInvoker"/> over the source list, the binding name, and the body node.
/// </summary>
/// <param name="invoke">The binding builtin's evaluator.</param>
/// <param name="source">The node producing the list to iterate.</param>
/// <param name="binding">The per-element variable name introduced for <paramref name="body"/>.</param>
/// <param name="body">The predicate / projection / key node evaluated once per element in a <see cref="BindingScope"/>.</param>
internal sealed class BindingCallNode(
    BindingBuiltinInvoker invoke, ExpressionNode source, string binding, ExpressionNode body) : ExpressionNode
{
    public override ValueTask<object?> EvaluateAsync(EvalContext ctx) => invoke(source, binding, body, ctx);
}
