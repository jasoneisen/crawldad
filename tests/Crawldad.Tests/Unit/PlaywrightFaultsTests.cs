using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Browser.Real;
using Microsoft.Playwright;

namespace Crawldad.Tests.Unit;

/// <summary>The <see cref="PlaywrightFaults"/> mapping onto the exception taxonomy, driven with synthetic
/// exceptions so every branch (timeout, crash, closed-target, non-crash passthrough, success) is covered without a
/// real browser. A real timeout is separately exercised end-to-end through the local adapter.</summary>
public class PlaywrightFaultsTests
{
    [Fact]
    public async Task Void_maps_timeout_crash_passthrough_and_success()
    {
        // System.TimeoutException (what Playwright throws for a wait) → retryable BrowserTimeoutException.
        var timeout = await Should.ThrowAsync<BrowserTimeoutException>(
            () => PlaywrightFaults.RunAsync(() => throw new TimeoutException("Timeout 300ms exceeded")));
        timeout.Message.ShouldBe("Timeout 300ms exceeded");

        // A crash PlaywrightException → retryable BrowserPageCrashedException (the reopen path).
        await Should.ThrowAsync<BrowserPageCrashedException>(
            () => PlaywrightFaults.RunAsync(() => throw new PlaywrightException("Page crashed")));

        // A closed-target PlaywrightException (an op that starts on an already-dead page — the internal
        // TargetClosedException's bare default message) → the same retryable BrowserPageCrashedException.
        await Should.ThrowAsync<BrowserPageCrashedException>(
            () => PlaywrightFaults.RunAsync(() => throw new PlaywrightException("Target page, context or browser has been closed")));

        // A non-crash PlaywrightException is left to propagate unchanged (a terminal engine error).
        await Should.ThrowAsync<PlaywrightException>(
            () => PlaywrightFaults.RunAsync(() => throw new PlaywrightException("net::ERR_ABORTED")));

        await Should.NotThrowAsync(() => PlaywrightFaults.RunAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task Generic_maps_timeout_crash_passthrough_and_success()
    {
        await Should.ThrowAsync<BrowserTimeoutException>(
            () => PlaywrightFaults.RunAsync(() => Fail<int>(new TimeoutException("t"))));

        await Should.ThrowAsync<BrowserPageCrashedException>(
            () => PlaywrightFaults.RunAsync(() => Fail<int>(new PlaywrightException("Target crashed"))));

        await Should.ThrowAsync<BrowserPageCrashedException>(
            () => PlaywrightFaults.RunAsync(() => Fail<int>(new PlaywrightException("Target page, context or browser has been closed"))));

        await Should.ThrowAsync<PlaywrightException>(
            () => PlaywrightFaults.RunAsync(() => Fail<int>(new PlaywrightException("net::ERR_FAILED"))));

        (await PlaywrightFaults.RunAsync(() => Task.FromResult(42))).ShouldBe(42);
    }

    private static Task<T> Fail<T>(Exception ex) => Task.FromException<T>(ex);
}
