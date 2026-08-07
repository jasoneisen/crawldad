using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// Maps Playwright's fault surface onto the §8.3 browser taxonomy so the interpreter classifies real-backend faults
/// exactly as it does the record/replay fake's scripted ones. Two conditions are recognised and both are
/// <b>retryable</b>:
/// <list type="bullet">
///   <item>a <see cref="System.TimeoutException"/> — what Playwright for .NET throws when a wait never resolves
///   (verified: a <c>Locator.WaitForAsync</c> timeout surfaces as the BCL <c>TimeoutException</c>, not a
///   <c>PlaywrightException</c>) → <see cref="BrowserTimeoutException"/>;</item>
///   <item>a <see cref="PlaywrightException"/> whose message names a page/target crash (§3.6) →
///   <see cref="BrowserPageCrashedException"/>, which the reopen path expects.</item>
/// </list>
/// Every other exception — an unmapped <see cref="PlaywrightException"/> (e.g. a navigation <c>net::ERR_*</c>), or a
/// non-Playwright fault such as a <c>CrawldadFailureException</c> thrown by an interpreter <c>trigger</c> block — is
/// left to propagate unchanged, so it surfaces as the terminal engine error it is.
/// </summary>
internal static class PlaywrightFaults
{
    /// <summary>Runs an effect, translating a Playwright timeout/crash into the browser taxonomy.</summary>
    /// <param name="operation">The Playwright call to guard.</param>
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
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">The Playwright call to guard.</param>
    /// <returns>The call's result on success.</returns>
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

    // The reference detects a crash by the PlaywrightException message ("Page crashed"/"Target crashed", §3.6); we
    // match "crash" case-insensitively so both Playwright phrasings map onto the reopen path.
    private static bool IsCrash(PlaywrightException ex) =>
        ex.Message.Contains("crash", StringComparison.OrdinalIgnoreCase);
}
