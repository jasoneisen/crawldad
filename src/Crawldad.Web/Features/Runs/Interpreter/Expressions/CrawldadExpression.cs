namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>
/// A parsed Crawldad expression (§7): the public entry point the interpreter work package consumes for every
/// <c>Expr</c> leaf. <see cref="Parse"/> is pure and static (the validation/safety boundary — it rejects unknown
/// builtins, wrong arities, and malformed syntax before any execution); <see cref="EvaluateAsync"/> runs the
/// parsed tree against a scope, async because DOM reads are. The parsed instance is immutable and reusable across
/// runs — it captures no scope.
/// </summary>
public sealed class CrawldadExpression
{
    /// <summary>The default per-evaluation step budget (CD-3/§12) when a scope names no stricter one: generous enough that
    /// no legitimate expression approaches it (a normal expression spends tens of units; a binding builtin over a large
    /// list, thousands), so it only ever bites a pathological breadth-heavy expression. A server-side constant — the run
    /// scope overrides it with the configured knob, and a payload can never raise it.</summary>
    public const int DefaultStepBudget = 1_000_000;

    private readonly ExpressionNode _root;

    private CrawldadExpression(ExpressionNode root, string source)
    {
        _root = root;
        Source = source;
    }

    /// <summary>The original source text, for run-failure surfacing (§10) and diagnostics.</summary>
    public string Source { get; }

    /// <summary>
    /// Parses <paramref name="source"/> into a reusable expression. Pure and total: any malformed input is an
    /// <see cref="ExpressionParseException"/> carrying a stable code and the failing position.
    /// </summary>
    /// <param name="source">The expression source (§7.1 grammar).</param>
    /// <returns>The parsed expression.</returns>
    /// <exception cref="ExpressionParseException">On unknown builtins, wrong arity, malformed syntax, or over-deep nesting.</exception>
    public static CrawldadExpression Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tokens = Lexer.Tokenize(source);
        var root = new Parser(tokens).ParseProgram();
        return new CrawldadExpression(root, source);
    }

    /// <summary>
    /// Evaluates the expression against <paramref name="scope"/>, producing a value-model value (<see langword="null"/>,
    /// <see cref="bool"/>, <see cref="long"/>, <see cref="double"/>, <see cref="string"/>, an array, a map, or an
    /// opaque handle). Terminal semantic failures are <see cref="ExpressionEvaluationException"/>s.
    /// </summary>
    /// <param name="scope">The read-only run scope + DOM access.</param>
    /// <param name="ct">Cancels in-flight DOM reads.</param>
    /// <returns>The evaluated value.</returns>
    /// <exception cref="ExpressionEvaluationException">On any terminal type/index/conversion/URL/identifier failure.</exception>
    public ValueTask<object?> EvaluateAsync(IEvalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        // A fresh fuel counter per top-level evaluation (CD-3/§12): the budget is per expression, not per run, sized by the
        // scope's configured knob. Every nested node spends from this same counter via EvalContext.
        return _root.EvaluateAsync(new EvalContext(scope, new ExpressionFuel(scope.ExpressionStepBudget), ct));
    }

    /// <summary>
    /// The <b>free</b> variable identifiers this expression reads — the bare names it resolves through scope, with any
    /// bound by an enclosing binding builtin (<c>filter</c>/<c>map</c>/…) excluded. A pure static walk (no scope, no
    /// evaluation) backing save-time defined-before-use validation (§12). Builtin function names never appear.
    /// </summary>
    /// <returns>The distinct free identifier names.</returns>
    public IReadOnlySet<string> FreeIdentifiers()
    {
        var into = new HashSet<string>(StringComparer.Ordinal);
        _root.CollectFreeIdentifiers(into, new HashSet<string>(StringComparer.Ordinal));
        return into;
    }
}
