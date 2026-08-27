namespace Crawldad.Api.Features.Runs.Interpreter;

/// <summary>How the interpreter handles a page crash under <c>config.retry.onPageCrashed</c>. The wire tokens
/// (<c>reopenPage</c>/<c>fail</c>) are the JSON Schema enum; <see cref="PageCrashHandling.TryParse"/> maps them, and an
/// absent <c>onPageCrashed</c> is <see cref="ReopenPage"/> — the pre-existing unconditional reopen, so an existing
/// payload is byte-for-byte unchanged. Orthogonal to <c>retryOn</c>: <c>retryOn</c> decides <b>whether</b> a
/// <c>pageCrashed</c> is retried, this decides <b>what happens to the page</b> before that retry.</summary>
internal enum PageCrashHandlingStrategy
{
    /// <summary>Close the crashed page and open a fresh one on the same session/context, then rebind — the historical
    /// behaviour, and the default. The retry re-runs the whole program against the fresh page.</summary>
    ReopenPage,

    /// <summary>Do not reopen — the crash fails the attempt on the page it crashed on. Whether that attempt is retried
    /// is left entirely to <c>retryOn</c>: with <c>pageCrashed</c> in <c>retryOn</c> the attempt retries per policy (on
    /// the not-reopened page); without it the crash is terminal (<c>retryable-exhausted</c>), never silently papered
    /// over by a reopen.</summary>
    Fail,
}

/// <summary>The pure <c>config.retry.onPageCrashed</c> token → strategy mapping — the crash-handling analogue of
/// <see cref="RetryBackoff"/>. Side-effect-free so the mapping is asserted directly, exactly like the backoff parser.</summary>
internal static class PageCrashHandling
{
    /// <summary>Maps an <c>onPageCrashed</c> wire token to its strategy — the JSON Schema enum kept in one place so the
    /// interpreter and the schema cannot drift. An unrecognised token yields <see langword="false"/> (rejected at
    /// save/validate time by the schema, and terminally on an inline run that skips it), with <see cref="PageCrashHandlingStrategy.ReopenPage"/>
    /// as the safe fallback the caller ignores in favour of rejecting.</summary>
    public static bool TryParse(string value, out PageCrashHandlingStrategy strategy)
    {
        switch (value)
        {
            case "reopenPage":
                strategy = PageCrashHandlingStrategy.ReopenPage;
                return true;
            case "fail":
                strategy = PageCrashHandlingStrategy.Fail;
                return true;
            default:
                strategy = PageCrashHandlingStrategy.ReopenPage;
                return false;
        }
    }
}
