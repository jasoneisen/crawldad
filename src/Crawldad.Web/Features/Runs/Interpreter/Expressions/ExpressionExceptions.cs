using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>The stable <c>code</c> slugs carried by expression failures, referenced symbolically rather than by
/// literal. Split into parse-time (static rejection at the safety boundary) and eval-time (terminal runtime failures
/// that reproduce the reference's C# throws).</summary>
public static class ExpressionErrorCodes
{
    // Parse-time (ExpressionParseException) — the static safety boundary rejects these before any execution.
    /// <summary>A called function name is not in the builtin registry.</summary>
    public const string UnknownFunction = "unknown_function";
    /// <summary>A builtin was called with an argument count no overload accepts.</summary>
    public const string WrongArity = "wrong_arity";
    /// <summary>The token stream is not a well-formed expression (unexpected/missing token).</summary>
    public const string SyntaxError = "syntax_error";
    /// <summary>Nesting exceeded the recursive-descent depth cap — a terminal parse error guarding against pathological input.</summary>
    public const string ExpressionTooDeep = "expression_too_deep";

    // Eval-time (ExpressionEvaluationException) — terminal runtime failures.
    /// <summary>An operator or builtin was applied to an operand type it rejects.</summary>
    public const string TypeError = "type_error";
    /// <summary>An array index was out of range, or applied to null — reproduces C# <c>IndexOutOfRangeException</c>.</summary>
    public const string IndexOutOfRange = "index_out_of_range";
    /// <summary>Integer division or remainder by zero.</summary>
    public const string DivisionByZero = "division_by_zero";
    /// <summary>A required integer conversion (<c>toInt</c>) failed or was given null.</summary>
    public const string IntConversionFailed = "int_conversion_failed";
    /// <summary>A URL builtin was given a string that is not a valid absolute URL.</summary>
    public const string InvalidUrl = "invalid_url";
    /// <summary>A bare identifier was not bound in scope at evaluation time.</summary>
    public const string UnknownIdentifier = "unknown_identifier";
    /// <summary>A regex pattern exceeded the size cap — rejected before matching to bound compile cost.</summary>
    public const string RegexTooLarge = "regex_too_large";
    /// <summary>A regex match exceeded the time budget — a catastrophic-backtracking abort surfaces as terminal, never a hang.</summary>
    public const string RegexTimeout = "regex_timeout";
    /// <summary>A single expression evaluation spent its per-evaluation step budget — a fuel-metered abort of a
    /// pathological (breadth-heavy) expression, defence in depth beyond the parse-time depth cap. Terminal, never a spin.</summary>
    public const string ExpressionBudgetExceeded = "expression_budget_exceeded";
}

/// <summary>A terminal failure raised while evaluating an expression. Never retried; surfaced to the caller with its
/// <see cref="Code"/> (one of the eval-time <see cref="ExpressionErrorCodes"/>).</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A code is mandatory; the parameterless/message-only/inner-exception constructors would allow codeless failures that break run-failure surfacing.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "A code is mandatory; see CA1032 justification.")]
public sealed class ExpressionEvaluationException : Exception
{
    /// <summary>Creates a terminal evaluation failure.</summary>
    public ExpressionEvaluationException(string code, string message)
        : base(message) => Code = code;

    /// <summary>The stable failure slug (an eval-time <see cref="ExpressionErrorCodes"/> value).</summary>
    public string Code { get; }
}

/// <summary>A parse-time failure raised while parsing an expression or template. The static safety boundary: an
/// unknown builtin, wrong arity, or malformed syntax is rejected here, before any execution, so a schema-valid
/// payload has a bounded, inspectable effect surface.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A code and position are mandatory; the standard codeless constructors would break run-failure surfacing.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "A code and position are mandatory; see CA1032 justification.")]
public sealed class ExpressionParseException : Exception
{
    /// <summary>Creates a parse failure anchored at a source position.</summary>
    public ExpressionParseException(string code, string message, int position)
        : base(message)
    {
        Code = code;
        Position = position;
    }

    /// <summary>The stable failure slug (a parse-time <see cref="ExpressionErrorCodes"/> value).</summary>
    public string Code { get; }

    /// <summary>Zero-based index into the source string where the failure was detected.</summary>
    public int Position { get; }
}
