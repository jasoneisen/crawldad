using Crawldad.Api.Features.Webhooks;

namespace Crawldad.Tests.Unit;

/// <summary>The exponential-backoff schedule: <c>BaseDelay · 2^(attempt-1)</c>, saturated at <c>MaxDelay</c> and
/// overflow-safe for a large attempt count.</summary>
public class WebhookRetryPolicyTests
{
    private static readonly WebhookDeliveryOptions _options = new()
    {
        BaseDelay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromSeconds(20),
    };

    [Theory]
    [InlineData(1, 2)]    // base
    [InlineData(2, 4)]    // 2 * base
    [InlineData(3, 8)]    // 4 * base
    [InlineData(4, 16)]   // 8 * base
    [InlineData(5, 20)]   // 16 * base = 32s, capped at 20s
    [InlineData(9, 20)]   // far past the cap
    public void Doubles_each_attempt_then_saturates(int failedAttempt, int expectedSeconds) =>
        WebhookRetryPolicy.Backoff(failedAttempt, _options).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void Is_overflow_safe_for_a_huge_attempt_count() =>
        WebhookRetryPolicy.Backoff(1000, _options).ShouldBe(_options.MaxDelay); // shift >= 62 guard
}
