using Crawldad.Web.Features.Webhooks;

namespace Crawldad.Tests.Unit;

/// <summary>The boot-time guard for the webhook delivery knobs: accepts sane values (including the code defaults) and
/// fails a non-positive delay/timeout, a max-attempts below 1, or a max-delay below the base — reporting every failure at once.</summary>
public class WebhookOptionsValidatorTests
{
    private readonly WebhookOptionsValidator _validator = new();

    private static WebhookOptions Options(int maxAttempts, double baseSecs, double maxSecs, double timeoutSecs) => new()
    {
        Delivery = new WebhookDeliveryOptions
        {
            MaxAttempts = maxAttempts,
            BaseDelay = TimeSpan.FromSeconds(baseSecs),
            MaxDelay = TimeSpan.FromSeconds(maxSecs),
            Timeout = TimeSpan.FromSeconds(timeoutSecs),
        },
    };

    [Fact]
    public void Accepts_the_defaults() =>
        _validator.Validate(null, new WebhookOptions()).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(0, 10, 300, 10)] // MaxAttempts < 1
    [InlineData(8, 0, 300, 10)]  // BaseDelay <= 0
    [InlineData(8, 300, 10, 10)] // MaxDelay < BaseDelay
    [InlineData(8, 10, 300, 0)]  // Timeout <= 0
    public void Rejects_a_bad_knob(int maxAttempts, double baseSecs, double maxSecs, double timeoutSecs) =>
        _validator.Validate(null, Options(maxAttempts, baseSecs, maxSecs, timeoutSecs)).Failed.ShouldBeTrue();

    [Fact]
    public void Reports_every_failure_at_once()
    {
        var result = _validator.Validate(null, Options(maxAttempts: 0, baseSecs: 0, maxSecs: 0, timeoutSecs: 0));

        result.Failed.ShouldBeTrue();
        result.Failures.Count().ShouldBeGreaterThanOrEqualTo(3); // max-attempts + base-delay + timeout
    }
}
