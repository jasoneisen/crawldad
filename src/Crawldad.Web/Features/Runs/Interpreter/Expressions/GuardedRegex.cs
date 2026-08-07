using System.Text.RegularExpressions;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>
/// The single guarded <see cref="Regex"/> factory for the whole engine (§7.2 "size-limited and timeout-guarded").
/// Every user-supplied pattern — <c>matches</c>, <c>replaceRegex</c>, and the interpreter's <c>filter.hasTextRegex</c>
/// selector path — must go through here so no expression can author a pattern that hangs the run.
///
/// <para>Two guards:</para>
/// <list type="bullet">
///   <item><b>Size cap</b> (<see cref="MaxPatternLength"/> chars) → terminal <c>regex_too_large</c>, bounding compile
///   cost before a single character is matched.</item>
///   <item><b>Match timeout</b> (<see cref="MatchTimeoutMs"/> ms) → a catastrophic-backtracking blow-up aborts as a
///   terminal <c>regex_timeout</c> instead of pinning a CPU.</item>
/// </list>
///
/// <para><b>Why a timeout, not <see cref="RegexOptions.NonBacktracking"/>.</b> NonBacktracking guarantees linear time
/// but <em>rejects</em> lookaround and backreference patterns at construction — it would narrow the language and change
/// match semantics for otherwise-valid patterns. A match timeout bounds runtime uniformly across every pattern the
/// grammar admits, so the surface stays exactly the reference's <c>Regex</c> semantics, only time-capped.</para>
/// </summary>
internal static class GuardedRegex
{
    /// <summary>The longest pattern accepted; longer patterns are a terminal <c>regex_too_large</c> before compilation.</summary>
    internal const int MaxPatternLength = 512;

    /// <summary>The per-match wall-clock budget; exceeding it is a terminal <c>regex_timeout</c>.</summary>
    internal const int MatchTimeoutMs = 250;

    private static TimeSpan MatchTimeout => TimeSpan.FromMilliseconds(MatchTimeoutMs);

    /// <summary>
    /// Compiles <paramref name="pattern"/> into a size- and time-guarded <see cref="Regex"/>. Culture-invariant, so
    /// matching is deterministic and consistent with the language's ordinal string operators.
    /// </summary>
    /// <param name="pattern">The user-supplied pattern.</param>
    /// <returns>A regex whose matches are bounded by <see cref="MatchTimeout"/>.</returns>
    /// <exception cref="ExpressionEvaluationException">When <paramref name="pattern"/> exceeds <see cref="MaxPatternLength"/> (<c>regex_too_large</c>).</exception>
    public static Regex Compile(string pattern)
    {
        EnsureWithinSizeLimit(pattern);
        return new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
    }

    /// <summary>
    /// Size-guards <paramref name="pattern"/> (same <see cref="MaxPatternLength"/> cap) and returns a <see cref="Regex"/>
    /// carrying <b>no .NET-specific options</b>, suitable for handing to an out-of-process matcher such as Playwright's
    /// browser-side <c>filter.hasTextRegex</c> — which rejects options like <see cref="RegexOptions.CultureInvariant"/>.
    /// The match-time guard is intentionally omitted (matching happens in the browser, not this process), so the size
    /// cap remains the language-boundary guarantee (§7.2).
    /// </summary>
    /// <param name="pattern">The user-supplied pattern.</param>
    /// <returns>A size-guarded, option-free regex the browser engine can serialize.</returns>
    /// <exception cref="ExpressionEvaluationException">When <paramref name="pattern"/> exceeds <see cref="MaxPatternLength"/> (<c>regex_too_large</c>).</exception>
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
    /// <param name="pattern">The user-supplied pattern.</param>
    /// <param name="input">The string to test.</param>
    /// <returns><see langword="true"/> when <paramref name="input"/> matches.</returns>
    /// <exception cref="ExpressionEvaluationException">On an oversized pattern (<c>regex_too_large</c>) or a match timeout (<c>regex_timeout</c>).</exception>
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
    /// <param name="pattern">The user-supplied pattern.</param>
    /// <param name="input">The string to search.</param>
    /// <param name="replacement">The .NET substitution replacement.</param>
    /// <returns>The input with every match replaced.</returns>
    /// <exception cref="ExpressionEvaluationException">On an oversized pattern (<c>regex_too_large</c>) or a match timeout (<c>regex_timeout</c>).</exception>
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
