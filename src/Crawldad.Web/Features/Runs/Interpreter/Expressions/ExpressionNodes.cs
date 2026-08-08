namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>The environment threaded through evaluation: the read-only <see cref="IEvalScope"/>, the run's cancellation
/// token, and the per-evaluation <see cref="Fuel"/> counter. A small struct so every node's <c>EvaluateAsync</c> takes one
/// argument; the <see cref="Fuel"/> is a shared reference (a mutable counter), so it accumulates across the whole tree even
/// though the struct is copied — and <c>ctx with { Scope = … }</c> in the binding builtins carries it forward unchanged.</summary>
/// <param name="Scope">The flat run scope reads resolve against.</param>
/// <param name="Fuel">The shared per-evaluation step counter (CD-3/§12).</param>
/// <param name="Ct">Cancels an in-flight DOM read.</param>
internal readonly record struct EvalContext(IEvalScope Scope, ExpressionFuel Fuel, CancellationToken Ct);

/// <summary>
/// The per-evaluation fuel counter (CD-3/§12): one instance is created for each top-level
/// <see cref="CrawldadExpression.EvaluateAsync"/> and shared (by reference) through the whole tree via
/// <see cref="EvalContext"/>. Every node evaluation spends one unit at the <see cref="ExpressionNode.EvaluateAsync"/>
/// chokepoint; overspending is a terminal <see cref="ExpressionErrorCodes.ExpressionBudgetExceeded"/>. A cheap increment
/// (no allocation per spend) that bounds a breadth-heavy but non-recursive expression — a binding builtin over a large
/// list — which the parse-time depth cap cannot. The budget is per evaluation, so it never accumulates across a run's many
/// expressions (that is what max-steps and the wall-clock deadline bound); a fresh counter starts each expression.
/// </summary>
/// <param name="budget">The maximum node evaluations this expression may spend.</param>
internal sealed class ExpressionFuel(int budget)
{
    private int _spent;

    /// <summary>Spends one unit; the first spend that crosses <paramref name="budget"/> is terminal.</summary>
    /// <exception cref="ExpressionEvaluationException">When the per-evaluation budget is exhausted.</exception>
    public void Spend()
    {
        if (++_spent > budget)
        {
            throw new ExpressionEvaluationException(
                ExpressionErrorCodes.ExpressionBudgetExceeded,
                $"expression evaluation exceeded its {budget}-step budget");
        }
    }
}

/// <summary>
/// One node of a parsed expression tree (§7.1). Evaluation is async because leaf DOM reads are; pure/arithmetic
/// nodes complete synchronously. A node captures no scope — the same parsed tree is safely reusable across runs.
/// </summary>
internal abstract class ExpressionNode
{
    /// <summary>Evaluates this node against <paramref name="ctx"/>, producing a value-model value or a terminal failure.
    /// The single fuel chokepoint (CD-3/§12): it spends one budget unit, then dispatches to the node's
    /// <see cref="EvaluateCoreAsync"/> — so every node evaluation (including a builtin's argument and a binding builtin's
    /// per-element body) is metered by construction, no per-node discipline required.</summary>
    /// <param name="ctx">The scope + cancellation token + fuel.</param>
    public ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        ctx.Fuel.Spend();
        return EvaluateCoreAsync(ctx);
    }

    /// <summary>Evaluates this node's own logic (children recurse back through <see cref="EvaluateAsync"/>, so they too are
    /// fuel-metered). Overridden per node kind.</summary>
    /// <param name="ctx">The scope + cancellation token + fuel.</param>
    protected abstract ValueTask<object?> EvaluateCoreAsync(EvalContext ctx);

    /// <summary>
    /// Collects the <b>free</b> variable identifiers this subtree reads — the bare names it resolves through scope,
    /// minus any bound by an enclosing binding builtin (<c>filter</c>/<c>map</c>/…). Backs save-time defined-before-use
    /// validation (§12); a pure static walk, no evaluation. Builtin function names are not identifiers (resolved at
    /// parse), so they never appear here.
    /// </summary>
    /// <param name="into">Accumulates the free identifier names.</param>
    /// <param name="bound">Names currently bound by enclosing binding builtins (excluded from <paramref name="into"/>).</param>
    public abstract void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound);
}

/// <summary>A constant: number (<see cref="long"/>/<see cref="double"/>), string, bool, or null.</summary>
internal sealed class LiteralNode(object? value) : ExpressionNode
{
    protected override ValueTask<object?> EvaluateCoreAsync(EvalContext ctx) => new(value);

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        // A literal reads no variables.
    }
}

/// <summary>A bare identifier resolved through scope; unbound at eval time is a terminal <c>unknown_identifier</c>.</summary>
internal sealed class IdentifierNode(string name) : ExpressionNode
{
    /// <summary>The identifier text — read by the parser when this node fills a binding builtin's binding slot.</summary>
    public string Name { get; } = name;

    protected override ValueTask<object?> EvaluateCoreAsync(EvalContext ctx) =>
        ctx.Scope.TryResolve(Name, out var value)
            ? new ValueTask<object?>(value)
            : throw new ExpressionEvaluationException(
                ExpressionErrorCodes.UnknownIdentifier, $"unknown identifier '{Name}'");

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        if (!bound.Contains(Name))
        {
            into.Add(Name);
        }
    }
}

/// <summary>An array literal <c>[…]</c> → a fresh <see cref="List{T}"/> of the evaluated elements.</summary>
internal sealed class ArrayNode(IReadOnlyList<ExpressionNode> items) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx)
    {
        var list = new List<object?>(items.Count);
        foreach (var item in items)
        {
            list.Add(await item.EvaluateAsync(ctx));
        }

        return list;
    }

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        foreach (var item in items)
        {
            item.CollectFreeIdentifiers(into, bound);
        }
    }
}

/// <summary>An object literal <c>{ k: Expr }</c> → an insertion-ordered <see cref="Dictionary{TKey, TValue}"/>.</summary>
internal sealed class ObjectNode(IReadOnlyList<KeyValuePair<string, ExpressionNode>> entries) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, node) in entries)
        {
            map[key] = await node.EvaluateAsync(ctx);
        }

        return map;
    }

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        // Keys are literal strings, not references; only the value nodes can read variables.
        foreach (var (_, node) in entries)
        {
            node.CollectFreeIdentifiers(into, bound);
        }
    }
}

/// <summary>Logical negation <c>!x</c>: operand must be bool (§7.1).</summary>
internal sealed class NotNode(ExpressionNode operand) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx) =>
        !ExpressionValues.RequireBool(await operand.EvaluateAsync(ctx));

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound) =>
        operand.CollectFreeIdentifiers(into, bound);
}

/// <summary>Arithmetic negation <c>-x</c>: operand must be a number, else a terminal <c>type_error</c>.</summary>
internal sealed class NegateNode(ExpressionNode operand) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx)
    {
        var value = await operand.EvaluateAsync(ctx);
        return value switch
        {
            long l => (object)unchecked(-l),
            double d => -d,
            _ => throw ExpressionValues.TypeError($"unary '-' requires a number, got {ExpressionValues.TypeName(value)}"),
        };
    }

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound) =>
        operand.CollectFreeIdentifiers(into, bound);
}

/// <summary>An eager binary operator (<c>+ - * / % == != &lt; &lt;= &gt; &gt;=</c>) applied via a value-model delegate.</summary>
internal sealed class BinaryNode(ExpressionNode left, ExpressionNode right, Func<object?, object?, object?> op) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx) =>
        op(await left.EvaluateAsync(ctx), await right.EvaluateAsync(ctx));

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        left.CollectFreeIdentifiers(into, bound);
        right.CollectFreeIdentifiers(into, bound);
    }
}

/// <summary>Short-circuiting <c>&amp;&amp;</c>: both operands must be bool; the right is not evaluated when the left is false.</summary>
internal sealed class AndNode(ExpressionNode left, ExpressionNode right) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx)
    {
        if (!ExpressionValues.RequireBool(await left.EvaluateAsync(ctx)))
        {
            return false;
        }

        return ExpressionValues.RequireBool(await right.EvaluateAsync(ctx));
    }

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        left.CollectFreeIdentifiers(into, bound);
        right.CollectFreeIdentifiers(into, bound);
    }
}

/// <summary>Short-circuiting <c>||</c>: both operands must be bool; the right is not evaluated when the left is true.</summary>
internal sealed class OrNode(ExpressionNode left, ExpressionNode right) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx)
    {
        if (ExpressionValues.RequireBool(await left.EvaluateAsync(ctx)))
        {
            return true;
        }

        return ExpressionValues.RequireBool(await right.EvaluateAsync(ctx));
    }

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        left.CollectFreeIdentifiers(into, bound);
        right.CollectFreeIdentifiers(into, bound);
    }
}

/// <summary>Ternary <c>c ? a : b</c>: the condition must be bool; only the taken branch is evaluated.</summary>
internal sealed class TernaryNode(ExpressionNode condition, ExpressionNode ifTrue, ExpressionNode ifFalse) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx) =>
        ExpressionValues.RequireBool(await condition.EvaluateAsync(ctx))
            ? await ifTrue.EvaluateAsync(ctx)
            : await ifFalse.EvaluateAsync(ctx);

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        condition.CollectFreeIdentifiers(into, bound);
        ifTrue.CollectFreeIdentifiers(into, bound);
        ifFalse.CollectFreeIdentifiers(into, bound);
    }
}

/// <summary>Member access <c>a.b</c>: map → value (absent key → null), null → null (models C# <c>?.</c>), else <c>type_error</c>.</summary>
internal sealed class MemberNode(ExpressionNode target, string name) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx)
    {
        var value = await target.EvaluateAsync(ctx);
        return value switch
        {
            null => null,
            Dictionary<string, object?> map => map.GetValueOrDefault(name),
            _ => throw ExpressionValues.TypeError($"cannot access member '.{name}' on {ExpressionValues.TypeName(value)}"),
        };
    }

    // The member name is a fixed key, not a variable reference; only the target subtree reads variables.
    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound) =>
        target.CollectFreeIdentifiers(into, bound);
}

/// <summary>
/// Index access <c>a[i]</c>: array with an integer index (out of range, or on null → terminal
/// <c>index_out_of_range</c>, reproducing C# <c>IndexOutOfRangeException</c>); map with a string index → value or
/// null; anything else → <c>type_error</c> (§7.2).
/// </summary>
internal sealed class IndexNode(ExpressionNode target, ExpressionNode index) : ExpressionNode
{
    protected override async ValueTask<object?> EvaluateCoreAsync(EvalContext ctx)
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

    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        target.CollectFreeIdentifiers(into, bound);
        index.CollectFreeIdentifiers(into, bound);
    }
}

/// <summary>A builtin invocation. The builtin is resolved at parse time (unknown/wrong-arity are parse errors), so
/// this node just runs the pre-bound <see cref="BuiltinInvoker"/> over its argument nodes.</summary>
internal sealed class CallNode(BuiltinInvoker invoke, IReadOnlyList<ExpressionNode> args) : ExpressionNode
{
    protected override ValueTask<object?> EvaluateCoreAsync(EvalContext ctx) => invoke(args, ctx);

    // The function name is resolved at parse (not a variable); only the argument subtrees read variables.
    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        foreach (var arg in args)
        {
            arg.CollectFreeIdentifiers(into, bound);
        }
    }
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
    protected override ValueTask<object?> EvaluateCoreAsync(EvalContext ctx) => invoke(source, binding, body, ctx);

    // The source is read in the outer scope; the body is read with `binding` locally bound (so it is not a free
    // reference there). Add/remove around the body traversal so nested/shadowing bindings resolve correctly.
    public override void CollectFreeIdentifiers(ISet<string> into, ISet<string> bound)
    {
        source.CollectFreeIdentifiers(into, bound);
        var added = bound.Add(binding);
        body.CollectFreeIdentifiers(into, bound);
        if (added)
        {
            bound.Remove(binding);
        }
    }
}
