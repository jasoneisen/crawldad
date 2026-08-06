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
        return _root.EvaluateAsync(new EvalContext(scope, ct));
    }
}
