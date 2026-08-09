using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    /// The top-level <c>input</c> keys this expression reads via a direct <c>input.&lt;key&gt;</c> / <c>input["key"]</c>
    /// access (CD-6): the static half of the guarantee that a <c>secretRef</c> input is consumed only by <c>fill.secret</c>.
    /// A pure static walk, no evaluation.
    /// </summary>
    /// <returns>The distinct referenced <c>input</c> key names.</returns>
    public IReadOnlySet<string> InputMemberReferences()
    {
        var into = new HashSet<string>(StringComparer.Ordinal);
        _root.CollectInputMembers(into, new HashSet<string>(StringComparer.Ordinal));
        return into;
    }

    /// <summary>
    /// When this expression is <b>exactly</b> a single <c>input.&lt;name&gt;</c> (or <c>input["name"]</c>) reference — the
    /// only shape a <c>fill.secret</c> may take (CD-6) — yields that input name. Any richer expression (a call, an operator,
    /// a nested access, a bare identifier) is rejected, so <c>fill.secret</c> is a restricted reference, never the general
    /// expression channel a secret must be structurally unable to enter.
    /// </summary>
    /// <param name="inputName">The referenced secretRef input name when the shape matches.</param>
    /// <returns><see langword="true"/> when the root is a lone <c>input.&lt;name&gt;</c> reference.</returns>
    public bool TryGetInputMemberReference([NotNullWhen(true)] out string? inputName)
    {
        switch (_root)
        {
            case MemberNode { Target: IdentifierNode { Name: "input" }, Name: var name }:
                inputName = name;
                return true;
            case IndexNode { Target: IdentifierNode { Name: "input" }, Index: LiteralNode { Value: string key } }:
                inputName = key;
                return true;
            default:
                inputName = null;
                return false;
        }
    }

    /// <summary>
    /// When this expression is a compile-time-constant numeric literal — a bare number, or a unary minus applied to one
    /// (the two shapes the parser produces for <c>2.5</c> and <c>-2.5</c>) — yields its signed <paramref name="value"/>
    /// and whether it is <paramref name="isIntegral"/> (a <see cref="long"/>, or a <see cref="double"/> with no
    /// fractional part such as <c>2.0</c>). Any richer expression (a call, an operator over non-literals, an identifier,
    /// a non-numeric literal) is not a constant number and returns <see langword="false"/>. Backs the save-time
    /// rejection of a non-integral <c>loop.for</c> bound (#33): a fractional literal is caught statically, while a
    /// computed bound is left to the run-time integral check. Pure static inspection, no evaluation.
    /// </summary>
    /// <param name="value">The constant numeric value (sign applied) when the expression is a numeric literal.</param>
    /// <param name="isIntegral">Whether that value is a whole number the integer loop counter can take.</param>
    /// <returns><see langword="true"/> when the root is a bare numeric literal, optionally negated.</returns>
    public bool TryGetConstantNumber(out double value, out bool isIntegral)
    {
        var (literal, sign) = _root is NegateNode negate ? (negate.Operand, -1.0) : (_root, 1.0);
        switch (literal)
        {
            case LiteralNode { Value: long l }:
                value = sign * l;
                isIntegral = true;
                return true;
            case LiteralNode { Value: double d }:
                // A source literal is finite in every realistic case (the lexer parses a bounded digit string), so no
                // infinity guard is needed here — a pathological overflow-to-infinity literal reads as "integral" and is
                // harmlessly deferred to RunInterpreter.RequireIntegralBound, whose guard rejects it at run time.
                value = sign * d;
                isIntegral = d == Math.Floor(d);
                return true;
            default:
                value = 0;
                isIntegral = false;
                return false;
        }
    }
}
