using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>Maps Playwright faults onto the browser taxonomy: a <see cref="System.TimeoutException"/> becomes a
/// retryable <see cref="BrowserTimeoutException"/>, a crash- or closed-target-named <see cref="PlaywrightException"/>
/// becomes a retryable <see cref="BrowserPageCrashedException"/>; every other exception propagates unchanged as terminal.</summary>
internal static class PlaywrightFaults
{
    /// <summary>Runs an effect, translating a Playwright timeout/crash into the browser taxonomy.</summary>
    public static async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (TimeoutException ex)
        {
            throw new BrowserTimeoutException(ex.Message);
        }
        catch (PlaywrightException ex) when (IsCrashOrClosed(ex))
        {
            throw new BrowserPageCrashedException(ex.Message);
        }
    }

    /// <summary>Runs a value-producing call, translating a Playwright timeout/crash into the browser taxonomy.</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (TimeoutException ex)
        {
            throw new BrowserTimeoutException(ex.Message);
        }
        catch (PlaywrightException ex) when (IsCrashOrClosed(ex))
        {
            throw new BrowserPageCrashedException(ex.Message);
        }
    }

    // A dead page surfaces two deterministic ways, both mapped onto the retryable pageCrashed path. An IN-FLIGHT op on a
    // page that crashes under it sees a crash phrasing ("Page crashed"/"Target crashed…") — "crash", case-insensitive. An
    // op that STARTS on an already-dead target (e.g. re-driving the same page after onPageCrashed:"fail") never gets that
    // phrasing; it sees the bare default "Target page, context or browser has been closed" — "has been closed" is the
    // escape that would otherwise propagate as a raw PlaywrightException. TargetClosedException itself is internal to
    // Playwright, so — like the crash arm — this must match on the message, not the type. net::ERR_* engine errors carry
    // neither phrase and stay terminal passthroughs.
    private static bool IsCrashOrClosed(PlaywrightException ex) =>
        ex.Message.Contains("crash", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("has been closed", StringComparison.OrdinalIgnoreCase);
}
