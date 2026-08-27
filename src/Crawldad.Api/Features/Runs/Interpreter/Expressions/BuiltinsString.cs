namespace Crawldad.Api.Features.Runs.Interpreter.Expressions;

/// <summary>The string surface. Every builtin null-propagates on its primary string argument, except
/// <c>equalsIgnoreCase</c> which is null-safe (both-null true, one-null false). Comparisons are ordinal; out-of-range
/// access is a terminal <c>index_out_of_range</c>, never silent.</summary>
internal static partial class BuiltinRegistry
{
    private static IEnumerable<Builtin> StringBuiltins() =>
    [
        Fn3("replace", Replace),
        Fn3("replaceRegex", ReplaceRegex),
        StringBinary("split", Split),
        new Builtin("substring", 2, 3, SubstringAsync),
        StringBinary("substringAfterLast", SubstringAfterLast),
        StringBinary("endsWith", static (s, p) => s.EndsWith(p, StringComparison.Ordinal)),
        StringBinary("indexOf", static (s, x) => (long)s.IndexOf(x, StringComparison.Ordinal)),
        StringBinary("lastIndexOf", static (s, x) => (long)s.LastIndexOf(x, StringComparison.Ordinal)),
        StringBinary("matches", static (s, re) => GuardedRegex.IsMatch(re, s)),
        Fn2("equalsIgnoreCase", EqualsIgnoreCase),
        Fn2("join", Join),
    ];

    /// <summary>A two-argument string builtin: null primary → null; otherwise both arguments must be strings, else a
    /// terminal <c>type_error</c>. Centralises the null/type guard shared by the ordinal string operations.</summary>
    private static Builtin StringBinary(string name, Func<string, string, object?> fn) =>
        Fn2(name, (value, arg) => value switch
        {
            null => null,
            string s when arg is string a => fn(s, a),
            _ => throw ExpressionValues.TypeError(
                $"{name} expects strings, got {ExpressionValues.TypeName(value)} and {ExpressionValues.TypeName(arg)}"),
        });

    // replace(s, old, new) — ordinal, all occurrences (C# string.Replace). Empty `old` is a terminal
    // type_error, reproducing C# string.Replace's ArgumentException (the reference never passes an empty search).
    private static object? Replace(object? value, object? oldValue, object? newValue)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s && oldValue is string o && newValue is string n)
        {
            return o.Length == 0
                ? throw ExpressionValues.TypeError("replace search string cannot be empty")
                : s.Replace(o, n, StringComparison.Ordinal);
        }

        throw ExpressionValues.TypeError(
            $"replace expects strings, got {ExpressionValues.TypeName(value)}, {ExpressionValues.TypeName(oldValue)}, {ExpressionValues.TypeName(newValue)}");
    }

    // replaceRegex(s, re, rep) — C# Regex.Replace through the guarded factory (size cap + match timeout).
    private static object? ReplaceRegex(object? value, object? pattern, object? replacement)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s && pattern is string re && replacement is string rep)
        {
            return GuardedRegex.Replace(re, s, rep);
        }

        throw ExpressionValues.TypeError(
            $"replaceRegex expects strings, got {ExpressionValues.TypeName(value)}, {ExpressionValues.TypeName(pattern)}, {ExpressionValues.TypeName(replacement)}");
    }

    // split(s, sep) → array — C# string.Split(string): keeps empty entries.
    private static object Split(string s, string separator) => new List<object?>(s.Split(separator));

    // substring(s, a, b?) — Crawldad (start, endExclusive), NOT C# (start, length): substring(s,2) = s[2..];
    // substring(s,0,length(s)-1) = s[..^1]; substring(s,1,2) = the char at index 1. Out-of-range start/end is a
    // terminal index_out_of_range (C# range slicing throws). 2-arg form runs to the end of the string.
    private static async ValueTask<object?> SubstringAsync(IReadOnlyList<ExpressionNode> args, EvalContext ctx)
    {
        var value = await args[0].EvaluateAsync(ctx);
        if (value is null)
        {
            return null;
        }

        if (value is not string s)
        {
            throw ExpressionValues.TypeError($"substring expects a string, got {ExpressionValues.TypeName(value)}");
        }

        var start = ExpressionValues.RequireIndex(await args[1].EvaluateAsync(ctx), "substring start");
        var end = args.Count > 2
            ? ExpressionValues.RequireIndex(await args[2].EvaluateAsync(ctx), "substring end")
            : s.Length;

        if (start < 0 || end < start || end > s.Length)
        {
            throw new ExpressionEvaluationException(
                ExpressionErrorCodes.IndexOutOfRange,
                $"substring range [{start}, {end}) is out of range for a string of length {s.Length}");
        }

        return s[(int)start..(int)end];
    }

    // substringAfterLast(s, sep) = s[(s.LastIndexOf(sep)+1)..] — the whole string when sep is absent (LastIndexOf → -1).
    // Handy after a `contains(filename, '.')` guard. An empty sep reproduces C#'s throw (LastIndexOf("") == length ⇒
    // start past the end ⇒ terminal index_out_of_range).
    private static object SubstringAfterLast(string s, string separator)
    {
        var start = s.LastIndexOf(separator, StringComparison.Ordinal) + 1;
        return start > s.Length
            ? throw new ExpressionEvaluationException(
                ExpressionErrorCodes.IndexOutOfRange,
                $"substringAfterLast start {start} is out of range for a string of length {s.Length}")
            : s[start..];
    }

    // equalsIgnoreCase(a, b) — OrdinalIgnoreCase, null-safe like C# string.Equals(a, b, …): both-null true, one-null
    // false. NOT null-propagating.
    private static object? EqualsIgnoreCase(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is string sa && b is string sb)
        {
            return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        }

        throw ExpressionValues.TypeError(
            $"equalsIgnoreCase expects strings, got {ExpressionValues.TypeName(a)} and {ExpressionValues.TypeName(b)}");
    }

    // join(list, sep) — string.Join over the elements, each converted under string(x) rules (null → ""). Null list
    // propagates to null; a non-array list or non-string separator is a terminal type_error.
    private static object? Join(object? list, object? separator)
    {
        if (list is null)
        {
            return null;
        }

        if (list is not List<object?> items)
        {
            throw ExpressionValues.TypeError($"join expects an array, got {ExpressionValues.TypeName(list)}");
        }

        if (separator is not string sep)
        {
            throw ExpressionValues.TypeError($"join separator must be a string, got {ExpressionValues.TypeName(separator)}");
        }

        return string.Join(sep, items.Select(ExpressionValues.ToStringValue));
    }
}
