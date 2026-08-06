using System.Globalization;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>
/// The value-model operations (§7.1): the runtime semantics shared by the operator AST nodes and the builtins.
/// Runtime values are <see langword="null"/>, <see cref="bool"/>, <see cref="long"/> (integer literals),
/// <see cref="double"/> (decimal literals), <see cref="string"/>, <see cref="List{T}"/> of <see cref="object"/>
/// (array), <see cref="Dictionary{TKey, TValue}"/> of <see cref="string"/> to <see cref="object"/> (map,
/// insertion-ordered), and opaque handles (any other object). These helpers reproduce the reference's C#
/// behaviour exactly, including which mismatches are terminal <c>type_error</c>s.
/// </summary>
internal static class ExpressionValues
{
    /// <summary>A friendly type name for error messages. The <c>_</c> arm is an opaque handle (a bound locator).</summary>
    /// <param name="value">The value to name.</param>
    /// <returns>One of <c>null/bool/int/double/string/array/map/handle</c>.</returns>
    public static string TypeName(object? value) => value switch
    {
        null => "null",
        bool => "bool",
        long => "int",
        double => "double",
        string => "string",
        List<object?> => "array",
        Dictionary<string, object?> => "map",
        _ => "handle",
    };

    /// <summary>True when <paramref name="value"/> is one of the numeric runtime types (<see cref="long"/> or <see cref="double"/>).</summary>
    /// <param name="value">The value to test.</param>
    public static bool IsNumber(object? value) => value is long or double;

    private static bool IsScalar(object? value) => value is bool or long or double or string;

    private static double ToDouble(object? value) => value is long l ? l : (double)value!;

    /// <summary>
    /// The <c>string(x)</c> conversion (§7.1): null→<c>""</c>, bool→<c>true/false</c>, int→invariant digits,
    /// double→invariant round-trip, string→itself. Array/map/handle are a terminal <c>type_error</c>.
    /// </summary>
    /// <param name="value">The value to stringify.</param>
    /// <returns>The string form.</returns>
    /// <exception cref="ExpressionEvaluationException">When <paramref name="value"/> is an array, map, or handle.</exception>
    public static string ToStringValue(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        string s => s,
        _ => throw TypeError($"string() cannot convert {TypeName(value)}"),
    };

    /// <summary>Coerces to bool for logical operators and condition positions (§7.1): non-bool (incl. null) is a terminal <c>type_error</c>.</summary>
    /// <param name="value">The value that must be a bool.</param>
    /// <returns>The unwrapped bool.</returns>
    /// <exception cref="ExpressionEvaluationException">When <paramref name="value"/> is not a bool.</exception>
    public static bool RequireBool(object? value) =>
        value is bool b ? b : throw TypeError($"expected bool, got {TypeName(value)}");

    /// <summary>
    /// The <c>+</c> operator (§7.1): if either operand is a string it concatenates (converting the other with
    /// <see cref="ToStringValue"/> rules); otherwise it is numeric addition; otherwise a terminal <c>type_error</c>.
    /// </summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The concatenated string or numeric sum.</returns>
    public static object? Add(object? left, object? right) =>
        left is string || right is string
            ? ToStringValue(left) + ToStringValue(right)
            : Arithmetic(left, right, static (a, b) => unchecked(a + b), static (a, b) => a + b);

    /// <summary>The <c>-</c> operator: numeric only, else terminal <c>type_error</c>.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static object? Subtract(object? left, object? right) =>
        Arithmetic(left, right, static (a, b) => unchecked(a - b), static (a, b) => a - b);

    /// <summary>The <c>*</c> operator: numeric only, else terminal <c>type_error</c>.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static object? Multiply(object? left, object? right) =>
        Arithmetic(left, right, static (a, b) => unchecked(a * b), static (a, b) => a * b);

    /// <summary>The <c>/</c> operator: integer division on <see cref="long"/>s (division by zero is terminal), IEEE on doubles.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static object? Divide(object? left, object? right) =>
        Arithmetic(left, right, static (a, b) => b == 0 ? throw DivByZero() : a / b, static (a, b) => a / b);

    /// <summary>The <c>%</c> operator: integer remainder on <see cref="long"/>s (remainder by zero is terminal), IEEE on doubles.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static object? Modulo(object? left, object? right) =>
        Arithmetic(left, right, static (a, b) => b == 0 ? throw DivByZero() : a % b, static (a, b) => a % b);

    private static object Arithmetic(object? left, object? right, Func<long, long, long> longOp, Func<double, double, double> doubleOp)
    {
        if (left is long a && right is long b)
        {
            return longOp(a, b);
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return doubleOp(ToDouble(left), ToDouble(right));
        }

        throw TypeError($"arithmetic requires numbers, got {TypeName(left)} and {TypeName(right)}");
    }

    /// <summary>The <c>==</c> operator (§7.1): null-safe, numeric across int/double, ordinal string, bool; array/map/handle is terminal <c>type_error</c>.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool AreEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (!IsScalar(left) || !IsScalar(right))
        {
            throw TypeError($"cannot compare {TypeName(left)} and {TypeName(right)} for equality");
        }

        if (left is long la && right is long rb)
        {
            return la == rb;
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return ToDouble(left).Equals(ToDouble(right));
        }

        if (left is string ls && right is string rs)
        {
            return string.Equals(ls, rs, StringComparison.Ordinal);
        }

        if (left is bool lb && right is bool rbool)
        {
            return lb == rbool;
        }

        return false;
    }

    /// <summary>The <c>&lt;</c> operator: numbers only (§7.1), else terminal <c>type_error</c>.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool Less(object? left, object? right) => NumericCompare(left, right) < 0;

    /// <summary>The <c>&lt;=</c> operator: numbers only, else terminal <c>type_error</c>.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool LessOrEqual(object? left, object? right) => NumericCompare(left, right) <= 0;

    /// <summary>The <c>&gt;</c> operator: numbers only, else terminal <c>type_error</c>.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool Greater(object? left, object? right) => NumericCompare(left, right) > 0;

    /// <summary>The <c>&gt;=</c> operator: numbers only, else terminal <c>type_error</c>.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool GreaterOrEqual(object? left, object? right) => NumericCompare(left, right) >= 0;

    private static int NumericCompare(object? left, object? right)
    {
        if (left is long a && right is long b)
        {
            return a.CompareTo(b);
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return ToDouble(left).CompareTo(ToDouble(right));
        }

        throw TypeError($"relational operators require numbers, got {TypeName(left)} and {TypeName(right)}");
    }

    internal static ExpressionEvaluationException TypeError(string message) =>
        new(ExpressionErrorCodes.TypeError, message);

    private static ExpressionEvaluationException DivByZero() =>
        new(ExpressionErrorCodes.DivisionByZero, "division by zero");
}
