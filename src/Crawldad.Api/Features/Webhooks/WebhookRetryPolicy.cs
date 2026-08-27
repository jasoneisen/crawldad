namespace Crawldad.Api.Features.Webhooks;

/// <summary>The exponential-backoff schedule between delivery attempts. Pure and overflow-safe: the delay after the
/// <paramref name="failedAttempt"/>-th failed attempt is <c>BaseDelay · 2^(failedAttempt-1)</c>, saturated at
/// <c>MaxDelay</c> — computed on integer ticks with an explicit guard so a large attempt count can never overflow into a
/// negative or throwing <see cref="TimeSpan"/>.</summary>
internal static class WebhookRetryPolicy
{
    /// <summary>The backoff before the retry that follows <paramref name="failedAttempt"/> (1-based: the attempt that just
    /// failed). Never exceeds <see cref="WebhookDeliveryOptions.MaxDelay"/>.</summary>
    public static TimeSpan Backoff(int failedAttempt, WebhookDeliveryOptions options)
    {
        var baseTicks = options.BaseDelay.Ticks;
        var maxTicks = options.MaxDelay.Ticks;
        var shift = failedAttempt - 1;

        // Saturate rather than shift into overflow: once 2^shift would push baseTicks past maxTicks (or the shift itself
        // is out of range), the capped MaxDelay is the answer.
        if (shift >= 62 || baseTicks > maxTicks >> shift)
        {
            return options.MaxDelay;
        }

        return TimeSpan.FromTicks(baseTicks << shift);
    }
}
