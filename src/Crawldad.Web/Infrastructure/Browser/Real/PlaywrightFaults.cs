using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>Maps Playwright faults onto the browser taxonomy: a <see cref="System.TimeoutException"/> becomes a
/// retryable <see cref="BrowserTimeoutException"/>, a crash-named <see cref="PlaywrightException"/> becomes a
/// retryable <see cref="BrowserPageCrashedException"/>; every other exception propagates unchanged as terminal.</summary>
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
        catch (PlaywrightException ex) when (IsCrash(ex))
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
        catch (PlaywrightException ex) when (IsCrash(ex))
        {
            throw new BrowserPageCrashedException(ex.Message);
        }
    }

    // Playwright phrases a crash two ways ("Page crashed"/"Target crashed"), so this matches "crash"
    // case-insensitively to catch both onto the reopen path.
    private static bool IsCrash(PlaywrightException ex) =>
        ex.Message.Contains("crash", StringComparison.OrdinalIgnoreCase);
}
