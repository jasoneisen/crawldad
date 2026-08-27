using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Api.Features.Runs.Interpreter.Expressions;

/// <summary>A parsed Crawldad expression. <see cref="Parse"/> is pure/static and rejects unknown builtins, wrong
/// arities, and malformed syntax before execution; <see cref="EvaluateAsync"/> runs the tree against a scope (async
/// because DOM reads are). Immutable and reusable across runs — it captures no scope.</summary>
public sealed class CrawldadExpression
{
    /// <summary>The default per-evaluation step budget when a scope names no stricter one: generous enough that no
    /// legitimate expression approaches it, so it only bites a pathological breadth-heavy expression. A server-side
    /// constant — the run scope may lower it, but a payload can never raise it.</summary>
    public const int DefaultStepBudget = 1_000_000;

    private readonly ExpressionNode _root;

    private CrawldadExpression(ExpressionNode root, string source)
    {
        _root = root;
        Source = source;
    }

    /// <summary>The original source text, for run-failure surfacing and diagnostics.</summary>
    public string Source { get; }

    /// <summary>Parses <paramref name="source"/> into a reusable expression. Pure and total: unknown builtins, wrong
    /// arity, malformed syntax, or over-deep nesting all raise <see cref="ExpressionParseException"/> with a stable
    /// code and the failing position.</summary>
    public static CrawldadExpression Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tokens = Lexer.Tokenize(source);
        var root = new Parser(tokens).ParseProgram();
        return new CrawldadExpression(root, source);
    }

    /// <summary>Evaluates the expression against <paramref name="scope"/>, producing a value-model value (null, bool,
    /// number, string, array, map, or opaque handle). Terminal semantic failures raise
    /// <see cref="ExpressionEvaluationException"/>.</summary>
    public ValueTask<object?> EvaluateAsync(IEvalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        // A fresh fuel counter per top-level evaluation: the budget is per expression, not per run, sized by the
        // scope's configured knob. Every nested node spends from this same counter via EvalContext.
        return _root.EvaluateAsync(new EvalContext(scope, new ExpressionFuel(scope.ExpressionStepBudget), ct));
    }

    /// <summary>The free variable identifiers this expression reads (excluding any bound by an enclosing binding
    /// builtin like <c>filter</c>/<c>map</c>). A pure static walk backing save-time defined-before-use validation;
    /// builtin function names never appear.</summary>
    public IReadOnlySet<string> FreeIdentifiers()
    {
        var into = new HashSet<string>(StringComparer.Ordinal);
        _root.CollectFreeIdentifiers(into, new HashSet<string>(StringComparer.Ordinal));
        return into;
    }

    /// <summary>The top-level <c>input</c> keys this expression reads via a direct <c>input.&lt;key&gt;</c> /
    /// <c>input["key"]</c> access — the static half of guaranteeing a <c>secretRef</c> input is consumed only by
    /// <c>fill.secret</c>. A pure static walk.</summary>
    public IReadOnlySet<string> InputMemberReferences()
    {
        var into = new HashSet<string>(StringComparer.Ordinal);
        _root.CollectInputMembers(into, new HashSet<string>(StringComparer.Ordinal));
        return into;
    }

    /// <summary>When this expression is exactly a single <c>input.&lt;name&gt;</c> (or <c>input["name"]</c>) reference —
    /// the only shape <c>fill.secret</c> may take — yields that input name. Any richer expression is rejected, so a
    /// secret can never enter the general expression channel.</summary>
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

    /// <summary>When this expression is a constant numeric literal (optionally unary-minus negated), yields its signed
    /// value and whether it is integral. Any richer expression returns false. Backs save-time rejection of a
    /// non-integral <c>loop.for</c> bound — a computed bound still gets the run-time integral check.</summary>
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
