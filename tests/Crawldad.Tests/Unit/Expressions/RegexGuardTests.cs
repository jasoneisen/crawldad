using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>The regex guards: a size cap (terminal <c>regex_too_large</c>) and a match timeout (terminal
/// <c>regex_timeout</c>) so no authored pattern can hang a run. Exercised through <c>matches</c> and <c>replaceRegex</c>,
/// which share the single guarded factory the interpreter's <c>filter.hasTextRegex</c> selector also uses.</summary>
public class RegexGuardTests
{
    private static string BigPattern => new('a', GuardedRegex.MaxPatternLength + 1);

    // The canonical catastrophic-backtracking pattern against input that cannot match: exponential work, aborted at
    // the match-timeout budget rather than pinning a CPU.
    private static string EvilInput => new string('a', 32) + "!";

    [Fact]
    public async Task Oversized_pattern_is_rejected_before_matching_as_regex_too_large()
    {
        var scope = new FakeScope().With("p", BigPattern);
        (await Xp.EvalErrorAsync("matches('x', p)", scope)).Code.ShouldBe(ExpressionErrorCodes.RegexTooLarge);
        (await Xp.EvalErrorAsync("replaceRegex('x', p, 'y')", scope)).Code.ShouldBe(ExpressionErrorCodes.RegexTooLarge);
    }

    [Fact]
    public async Task A_pattern_at_the_size_cap_is_accepted()
    {
        // Exactly MaxPatternLength characters is allowed; only strictly-longer patterns are rejected.
        var scope = new FakeScope().With("p", new string('a', GuardedRegex.MaxPatternLength));
        (await Xp.EvalAsync("matches('', p)", scope)).ShouldBe(false);
    }

    [Fact]
    public async Task Catastrophic_backtracking_aborts_as_regex_timeout_via_matches()
    {
        var scope = new FakeScope().With("s", EvilInput);
        (await Xp.EvalErrorAsync("matches(s, '(a+)+$')", scope)).Code.ShouldBe(ExpressionErrorCodes.RegexTimeout);
    }

    [Fact]
    public async Task Catastrophic_backtracking_aborts_as_regex_timeout_via_replaceRegex()
    {
        var scope = new FakeScope().With("s", EvilInput);
        (await Xp.EvalErrorAsync("replaceRegex(s, '(a+)+$', 'X')", scope)).Code.ShouldBe(ExpressionErrorCodes.RegexTimeout);
    }

    [Fact]
    public async Task Well_behaved_patterns_match_and_replace_normally()
    {
        (await Xp.EvalAsync("matches('2', '^[0-9]+$')")).ShouldBe(true);
        (await Xp.EvalAsync("replaceRegex('a1b2', '[0-9]', '#')")).ShouldBe("a#b#");
    }
}
