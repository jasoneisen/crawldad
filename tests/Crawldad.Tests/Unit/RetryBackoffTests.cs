using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>The pure program-retry backoff schedule (<c>config.retry.backoff</c>): <c>constant</c>/<c>linear</c>/
/// <c>exponential</c> scaling of <c>delayMs</c> by the failed-attempt number, saturated at <c>maxDelayMs</c>,
/// overflow-safe for a large attempt count, plus full jitter. The wiring into <c>Task.Delay</c> is proven in
/// <see cref="RetryTests"/> against the recording clock; here the arithmetic is asserted directly.</summary>
public class RetryBackoffTests
{
    [Theory]
    // constant: the same base every attempt, regardless of n.
    [InlineData(1, 100)]
    [InlineData(2, 100)]
    [InlineData(5, 100)]
    public void Constant_holds_the_base_delay(int failedAttempt, int expected) =>
        RetryBackoff.DelayMs(RetryBackoffStrategy.Constant, 100, failedAttempt, null).ShouldBe(expected);

    [Theory]
    // linear: base · n.
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(3, 300)]
    [InlineData(4, 400)]
    public void Linear_scales_by_the_attempt_number(int failedAttempt, int expected) =>
        RetryBackoff.DelayMs(RetryBackoffStrategy.Linear, 100, failedAttempt, null).ShouldBe(expected);

    [Theory]
    // exponential: base · 2^(n-1) — doubling.
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(3, 400)]
    [InlineData(4, 800)]
    [InlineData(5, 1600)]
    public void Exponential_doubles_each_attempt(int failedAttempt, int expected) =>
        RetryBackoff.DelayMs(RetryBackoffStrategy.Exponential, 100, failedAttempt, null).ShouldBe(expected);

    [Theory]
    // maxDelayMs saturates each strategy once its computed delay would exceed the cap.
    [InlineData("constant", 1000, 1, 250)]     // base alone already tops the cap
    [InlineData("linear", 100, 10, 250)]       // 1000 → capped at 250
    [InlineData("exponential", 100, 5, 250)]   // 1600 → capped at 250
    public void The_cap_saturates_every_strategy(string strategy, int baseDelayMs, int failedAttempt, int cap) =>
        RetryBackoff.DelayMs(Strategy(strategy), baseDelayMs, failedAttempt, cap).ShouldBe(cap);

    [Theory]
    // A computed delay UNDER the cap passes through untouched (the cap is a ceiling, not a floor).
    [InlineData("linear", 2, 200)]
    [InlineData("exponential", 3, 400)]
    public void Under_the_cap_the_computed_delay_passes_through(string strategy, int failedAttempt, int expected) =>
        RetryBackoff.DelayMs(Strategy(strategy), 100, failedAttempt, 5000).ShouldBe(expected);

    [Theory]
    // delayMs 0 (no base wait) ⇒ 0 for every strategy/attempt — the historical "retry with no wait".
    [InlineData("constant")]
    [InlineData("linear")]
    [InlineData("exponential")]
    public void A_zero_base_yields_no_wait(string strategy) =>
        RetryBackoff.DelayMs(Strategy(strategy), 0, 5, 1000).ShouldBe(0);

    [Fact]
    public void Exponential_is_overflow_safe_for_a_huge_attempt_count()
    {
        RetryBackoff.DelayMs(RetryBackoffStrategy.Exponential, 100, 1000, 5000).ShouldBe(5000);          // shift ≥ 31 ⇒ the cap
        RetryBackoff.DelayMs(RetryBackoffStrategy.Exponential, 100, 1000, null).ShouldBe(int.MaxValue);  // uncapped ⇒ saturates at int.MaxValue, never overflows negative
    }

    [Theory]
    // full jitter maps a sample in [0,1) linearly onto [0, delay).
    [InlineData(0.0, 0)]
    [InlineData(0.5, 500)]
    [InlineData(0.999, 999)]
    public void Full_jitter_scales_the_delay_by_the_sample(double sample, int expected) =>
        RetryBackoff.FullJitter(1000, sample).ShouldBe(expected);

    [Fact]
    public void Full_jitter_stays_within_bounds_across_the_sample_range()
    {
        // Sweep the whole [0,1) draw space: every full-jitter result lands in [0, delay). A live run draws the sample
        // from Random.Shared; the mapping is what matters, so it is swept deterministically here.
        for (var sample = 0.0; sample < 1.0; sample += 0.001)
        {
            RetryBackoff.FullJitter(1000, sample).ShouldBeInRange(0, 1000);
        }
    }

    [Fact]
    public void TryParse_maps_every_shipped_token()
    {
        RetryBackoff.TryParse("constant", out var constant).ShouldBeTrue();
        constant.ShouldBe(RetryBackoffStrategy.Constant);
        RetryBackoff.TryParse("linear", out var linear).ShouldBeTrue();
        linear.ShouldBe(RetryBackoffStrategy.Linear);
        RetryBackoff.TryParse("exponential", out var exponential).ShouldBeTrue();
        exponential.ShouldBe(RetryBackoffStrategy.Exponential);
    }

    [Theory]
    [InlineData("Exponential")] // case-sensitive: the wire tokens are lowercase
    [InlineData("fibonacci")]
    [InlineData("")]
    public void TryParse_rejects_an_unknown_token(string token)
    {
        RetryBackoff.TryParse(token, out var strategy).ShouldBeFalse();
        strategy.ShouldBe(RetryBackoffStrategy.Constant); // the safe fallback the caller ignores in favour of rejecting
    }

    // Parses a wire token to its strategy for the parameterised cases (a private helper may take the internal enum where a
    // public [Theory] parameter cannot).
    private static RetryBackoffStrategy Strategy(string token)
    {
        RetryBackoff.TryParse(token, out var strategy).ShouldBeTrue();
        return strategy;
    }
}
