namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>The backoff strategy <c>config.retry.backoff</c> selects for the wait between program-retry attempts. The
/// wire tokens (<c>constant</c>/<c>linear</c>/<c>exponential</c>) are the JSON Schema enum; <see cref="RetryBackoff.TryParse"/>
/// maps them, and an absent <c>backoff</c> is <see cref="Constant"/> — the pre-backoff behaviour, so an existing payload
/// is byte-for-byte unchanged.</summary>
internal enum RetryBackoffStrategy
{
    /// <summary>The same <c>delayMs</c> before every retry — the historical behaviour, and the default.</summary>
    Constant,

    /// <summary><c>delayMs · n</c> before the retry after the n-th failed attempt (1-based): base, 2·base, 3·base, ….</summary>
    Linear,

    /// <summary><c>delayMs · 2^(n-1)</c> before the retry after the n-th failed attempt (1-based): base, 2·base, 4·base, …
    /// (factor 2, Polly's default and the sibling of <see cref="Webhooks.WebhookRetryPolicy"/>).</summary>
    Exponential,
}

/// <summary>The pure program-retry backoff schedule — the interpreter's <c>config.retry</c> analogue of
/// <see cref="Webhooks.WebhookRetryPolicy"/>. <see cref="DelayMs"/> is the deterministic, overflow-safe delay a strategy
/// dictates before the retry that follows a given failed attempt, saturated at an optional <c>maxDelayMs</c> cap;
/// <see cref="FullJitter"/> spreads that delay across <c>[0, delay]</c> when <c>jitter</c> is on. Side-effect-free so the
/// schedule is asserted directly, exactly like the webhook policy.</summary>
internal static class RetryBackoff
{
    /// <summary>Maps a <c>config.retry.backoff</c> wire token to its strategy — the JSON Schema enum kept in one place so
    /// the interpreter and the schema cannot drift. An unrecognised token yields <see langword="false"/> (rejected at
    /// save/validate time by the schema, and terminally on an inline run that skips it).</summary>
    public static bool TryParse(string value, out RetryBackoffStrategy strategy)
    {
        switch (value)
        {
            case "constant":
                strategy = RetryBackoffStrategy.Constant;
                return true;
            case "linear":
                strategy = RetryBackoffStrategy.Linear;
                return true;
            case "exponential":
                strategy = RetryBackoffStrategy.Exponential;
                return true;
            default:
                strategy = RetryBackoffStrategy.Constant;
                return false;
        }
    }

    /// <summary>The pre-jitter delay (ms) before the retry that follows <paramref name="failedAttempt"/> (1-based: the
    /// attempt that just failed), scaling <paramref name="baseDelayMs"/> by <paramref name="strategy"/> and saturating at
    /// <paramref name="maxDelayMs"/> (uncapped ⇒ <see cref="int.MaxValue"/>). Computed on <see langword="long"/> with an
    /// explicit exponential guard so a large attempt count can never overflow into a negative or absurd wait.</summary>
    public static int DelayMs(RetryBackoffStrategy strategy, int baseDelayMs, int failedAttempt, int? maxDelayMs)
    {
        var cap = maxDelayMs ?? int.MaxValue;
        if (baseDelayMs <= 0)
        {
            return 0; // no base wait ⇒ no backoff whatever the strategy/attempt (and it keeps the exponential shift below well-defined)
        }

        switch (strategy)
        {
            case RetryBackoffStrategy.Linear:
                return (int)Math.Min((long)baseDelayMs * failedAttempt, cap);

            case RetryBackoffStrategy.Exponential:
                var shift = failedAttempt - 1;

                // Saturate rather than shift into overflow: past shift 30 an int base·2^shift can top int.MaxValue, and
                // since baseDelayMs is ≥ 1 here base·2^31 already exceeds any int cap — so the cap is the answer.
                return shift >= 31 ? cap : (int)Math.Min((long)baseDelayMs << shift, cap);

            default: // Constant — the same base wait every time.
                return Math.Min(baseDelayMs, cap);
        }
    }

    /// <summary>Full jitter: spread a computed <paramref name="delayMs"/> uniformly across <c>[0, delayMs)</c> given a
    /// uniform <paramref name="sample"/> in <c>[0, 1)</c> (the caller draws it). The well-known AWS "Full Jitter" —
    /// de-correlating retriers so a fleet that failed together does not re-attempt in lockstep.</summary>
    public static int FullJitter(int delayMs, double sample) => (int)(delayMs * sample);
}
