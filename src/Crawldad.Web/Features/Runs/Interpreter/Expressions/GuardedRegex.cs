using System.Text.RegularExpressions;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>The single guarded <see cref="Regex"/> factory for the whole engine: every user-supplied pattern must go
/// through here so none can hang the run. A size cap bounds compile cost (<c>regex_too_large</c>) and a match timeout
/// bounds runtime (<c>regex_timeout</c>), not <see cref="RegexOptions.NonBacktracking"/> (which rejects valid lookaround/backreference patterns).</summary>
internal static class GuardedRegex
{
    /// <summary>The longest pattern accepted; longer patterns are a terminal <c>regex_too_large</c> before compilation.</summary>
    internal const int MaxPatternLength = 512;

    /// <summary>The per-match wall-clock budget; exceeding it is a terminal <c>regex_timeout</c>.</summary>
    internal const int MatchTimeoutMs = 250;

    private static TimeSpan MatchTimeout => TimeSpan.FromMilliseconds(MatchTimeoutMs);

    /// <summary>Compiles <paramref name="pattern"/> into a size- and time-guarded <see cref="Regex"/>. Culture-invariant,
    /// so matching is deterministic and consistent with the language's ordinal string operators.</summary>
    public static Regex Compile(string pattern)
    {
        EnsureWithinSizeLimit(pattern);
        return new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
    }

    /// <summary>Size-guards <paramref name="pattern"/> and returns an option-free <see cref="Regex"/> for an
    /// out-of-process matcher like Playwright's <c>filter.hasTextRegex</c> (which rejects .NET-only options). The
    /// match-time guard is intentionally omitted — matching happens in the browser, not this process.</summary>
    public static Regex CompileForBrowser(string pattern)
    {
        EnsureWithinSizeLimit(pattern);
        // No options flags (Playwright serializes RegexOptions to browser-side inline flags and rejects the ones the JS
        // engine has no equivalent for, such as CultureInvariant); the match timeout is retained for the analyzer and is
        // harmless — Playwright ignores it, matching in the browser.
        return new Regex(pattern, RegexOptions.None, MatchTimeout);
    }

    private static void EnsureWithinSizeLimit(string pattern)
    {
        if (pattern.Length > MaxPatternLength)
        {
            throw new ExpressionEvaluationException(
                ExpressionErrorCodes.RegexTooLarge,
                $"regex pattern is {pattern.Length} chars, exceeding the {MaxPatternLength}-char limit");
        }
    }

    /// <summary>Guarded <see cref="Regex.IsMatch(string)"/>: a match-time blow-up surfaces as terminal <c>regex_timeout</c>.</summary>
    public static bool IsMatch(string pattern, string input)
    {
        var regex = Compile(pattern);
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            throw Timeout(pattern);
        }
    }

    /// <summary>Guarded <see cref="Regex.Replace(string, string)"/>: a match-time blow-up surfaces as terminal <c>regex_timeout</c>.</summary>
    public static string Replace(string pattern, string input, string replacement)
    {
        var regex = Compile(pattern);
        try
        {
            return regex.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            throw Timeout(pattern);
        }
    }

    private static ExpressionEvaluationException Timeout(string pattern) =>
        new(ExpressionErrorCodes.RegexTimeout, $"regex match exceeded the {MatchTimeoutMs}ms budget for pattern '{pattern}'");
}
