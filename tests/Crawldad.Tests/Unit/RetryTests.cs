using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Browser.Fake;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>The retry/resilience layer: the retryable-vs-terminal classifier, the <c>retryOn</c> gate, retryable
/// exhaustion, and the page-crash reopen-and-rebind. Faults are scripted by the <c>inject-timeout</c>/<c>inject-crash</c>
/// fixtures; delays run through the frozen clock at 0ms (one real-clock case proves the delay path).</summary>
public class RetryTests
{
    // A tiny program: navigate the fixture form, click the (fault-injected) button, confirm the results page loaded.
    private static string RetryPayload(string retry) =>
        $$"""
        { "name": "t", "config": { "backend": "input.backend", "retry": {{retry}} },
          "steps": [ { "goto": { "url": "https://fixture.test/form" } }, { "click": { "selector": "#go" } } ],
          "result": "exists('#done')" }
        """;

    private static string Inputs(string fixture) =>
        $$"""{ "backend": { "adapter": "fake", "options": { "fixture": "{{fixture}}" } } }""";

    private static RunFailureDetail Fail(RunOutcome outcome)
    {
        outcome.Status.ShouldBe(RunStatus.Failed);
        return outcome.Failure!;
    }

    [Fact]
    public async Task Timeout_is_retried_until_it_succeeds_with_attempt_events()
    {
        // inject-timeout fails the first 2 triggers; maxAttempts 5 retries into success on the 3rd.
        var outcome = await Runner.RunAsync(
            RetryPayload("""{ "maxAttempts": 5, "delayMs": 0, "backoff": "constant", "retryOn": ["timeout","pageCrashed"], "onPageCrashed": "reopenPage" }"""),
            Inputs("inject-timeout"));

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Result!.Value.GetBoolean().ShouldBeTrue();

        var attempts = outcome.Events.OfType<RunAttemptFailed>().ToList();
        attempts.Select(a => a.Attempt).ShouldBe([1, 2]);
        attempts.ShouldAllBe(a => a.Code == "timeout");
    }

    [Fact]
    public async Task Timeout_that_never_clears_exhausts_to_retryable_exhausted()
    {
        // Only 2 attempts, but the fault fails both ⇒ exhaustion.
        var outcome = await Runner.RunAsync(
            RetryPayload("""{ "maxAttempts": 2, "delayMs": 0, "retryOn": ["timeout","pageCrashed"] }"""),
            Inputs("inject-timeout"));

        var failure = Fail(outcome);
        failure.Class.ShouldBe("retryable-exhausted");
        failure.Code.ShouldBe("timeout");
        outcome.Events.OfType<RunAttemptFailed>().Select(a => a.Attempt).ShouldBe([1]); // attempt 1 retried; attempt 2 exhausted
    }

    [Fact]
    public async Task Page_crash_reopens_a_fresh_page_on_the_same_session_and_rebinds()
    {
        var (outcome, backend) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 3, "delayMs": 0, "retryOn": ["timeout","pageCrashed"], "onPageCrashed": "reopenPage" }"""),
            Inputs("inject-crash"));

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Result!.Value.GetBoolean().ShouldBeTrue();
        outcome.Events.OfType<RunAttemptFailed>().Single().Code.ShouldBe("pageCrashed");

        var session = backend.LastSession!;
        session.Pages.Count.ShouldBe(2);                        // original + one reopen — the SAME session served both
        session.Pages[0].CloseAttempted.ShouldBeTrue();         // the crashed page was torn down
        session.Pages[1].ShouldNotBeSameAs(session.Pages[0]);   // the retry ran against the fresh page
    }

    [Fact]
    public async Task Retry_respects_the_retry_on_list()
    {
        // A timeout when only pageCrashed is retryable ⇒ not retried, surfaced as retryable-exhausted with no attempt events.
        var timeout = await Runner.RunAsync(
            RetryPayload("""{ "maxAttempts": 5, "delayMs": 0, "retryOn": ["pageCrashed"] }"""),
            Inputs("inject-timeout"));
        Fail(timeout).Class.ShouldBe("retryable-exhausted");
        timeout.Events.OfType<RunAttemptFailed>().ShouldBeEmpty();

        // A pageCrashed when only timeout is retryable ⇒ not retried, and the page is never reopened.
        var (crash, backend) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 5, "delayMs": 0, "retryOn": ["timeout"] }"""),
            Inputs("inject-crash"));
        Fail(crash).Code.ShouldBe("pageCrashed");
        backend.LastSession!.Pages.Count.ShouldBe(1); // no reopen
    }

    // ----- onPageCrashed handling (reopenPage default vs fail) ---------------

    [Fact]
    public async Task An_absent_on_page_crashed_reopens_exactly_like_reopenPage()
    {
        // The field defaults to reopenPage: with onPageCrashed omitted entirely, a crash still closes the page and opens
        // a fresh one on the same session — byte-for-byte the explicit-reopenPage behaviour asserted above.
        var (outcome, backend) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 3, "delayMs": 0, "retryOn": ["timeout","pageCrashed"] }"""),
            Inputs("inject-crash"));

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        var session = backend.LastSession!;
        session.Pages.Count.ShouldBe(2);                       // reopened, exactly like the explicit reopenPage case
        session.Pages[0].CloseAttempted.ShouldBeTrue();
        session.Pages[1].ShouldNotBeSameAs(session.Pages[0]);
    }

    [Fact]
    public async Task Page_crash_with_fail_retries_without_reopening_when_pageCrashed_is_retryable()
    {
        // onPageCrashed:"fail" opts OUT of the reopen: the crash still fails the attempt, and because pageCrashed is in
        // retryOn the attempt retries per policy — but on the page it crashed on, never a fresh one. (The fake's
        // per-session attempt counter clears the scripted fault on the retry, so the re-run from the top succeeds.)
        var (outcome, backend) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 3, "delayMs": 0, "retryOn": ["timeout","pageCrashed"], "onPageCrashed": "fail" }"""),
            Inputs("inject-crash"));

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Events.OfType<RunAttemptFailed>().Single().Code.ShouldBe("pageCrashed"); // the crash was retried, not reopened

        var session = backend.LastSession!;
        session.Pages.Count.ShouldBe(1);                  // no reopen — the SAME page served both attempts
        session.Pages[0].CloseAttempted.ShouldBeFalse();  // the crashed page was never torn down
    }

    [Fact]
    public async Task Page_crash_with_fail_and_pageCrashed_not_retryable_is_retryable_exhausted_without_reopening()
    {
        // fail + pageCrashed absent from retryOn: the crash is terminal on the first hit, with no reopen and no retry —
        // the fail-fast-on-a-crash posture. onPageCrashed never changes the classification (retryOn does), so the
        // taxonomy is exactly what reopenPage yields for an un-retried crash: retryable-exhausted, code pageCrashed.
        var (outcome, backend) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 3, "delayMs": 0, "retryOn": ["timeout"], "onPageCrashed": "fail" }"""),
            Inputs("inject-crash"));

        var failure = Fail(outcome);
        failure.Class.ShouldBe("retryable-exhausted");
        failure.Code.ShouldBe("pageCrashed");
        outcome.Events.OfType<RunAttemptFailed>().ShouldBeEmpty(); // not eligible for retry ⇒ no attempt events
        backend.LastSession!.Pages.Count.ShouldBe(1);             // no reopen
    }

    [Fact]
    public async Task An_unknown_on_page_crashed_option_is_a_terminal_failure_on_an_inline_run()
    {
        // An inline run skips the schema (which rejects the enum at save/validate time), so an unrecognised option is
        // classified terminally rather than silently reopening a page it never asked to reopen.
        var outcome = await Runner.RunAsync(
            RetryPayload("""{ "maxAttempts": 3, "delayMs": 0, "retryOn": ["timeout","pageCrashed"], "onPageCrashed": "restart" }"""),
            Inputs("inject-crash"));

        var failure = Fail(outcome);
        failure.Class.ShouldBe("terminal");
        failure.Code.ShouldBe("invalid_retry_on_page_crashed");
        outcome.Events.OfType<RunAttemptFailed>().ShouldBeEmpty(); // rejected before the first attempt ran
    }

    [Fact]
    public async Task A_terminal_guard_failure_is_never_retried()
    {
        // Retry is configured generously, but a terminal guard aborts on the first attempt.
        var outcome = await Runner.RunAsync(
            """
            { "name": "t", "config": { "backend": "input.backend", "retry": { "maxAttempts": 5, "delayMs": 0, "retryOn": ["timeout","pageCrashed"] } },
              "steps": [ { "goto": { "url": "https://fixture.test/form" } },
                         { "guard": { "cond": "false", "elseFail": { "class": "terminal", "code": "record_not_accessible", "message": "no" } } } ],
              "result": "null" }
            """,
            Inputs("inject-timeout"));

        var failure = Fail(outcome);
        failure.Class.ShouldBe("terminal");
        failure.Code.ShouldBe("record_not_accessible");
        outcome.Events.OfType<RunAttemptFailed>().ShouldBeEmpty(); // attempt count == 1
    }

    [Fact]
    public async Task Retry_block_without_fields_defaults_to_a_single_attempt()
    {
        // An empty retry block: maxAttempts→1, delayMs→0, retryOn→default. A timeout therefore exhausts at once.
        var outcome = await Runner.RunAsync(RetryPayload("{}"), Inputs("inject-timeout"));
        Fail(outcome).Class.ShouldBe("retryable-exhausted");
        outcome.Events.OfType<RunAttemptFailed>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Retry_delay_runs_through_the_time_provider()
    {
        // A real (tiny) delay exercises the Task.Delay path without a frozen-clock hang; 2 retries × 1ms is negligible.
        var (outcome, _) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 5, "delayMs": 1, "retryOn": ["timeout"] }"""),
            Inputs("inject-timeout"),
            TimeProvider.System);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
    }

    // ----- backoff strategies (delay sequence through the recording clock) ---

    [Theory]
    // The persistent fixture times out on every attempt, so all 4 attempts fail and the 3 gaps between them each back
    // off. constant holds delayMs; linear scales it by the attempt; exponential doubles it.
    [InlineData("constant", new[] { 100, 100, 100 })]
    [InlineData("linear", new[] { 100, 200, 300 })]
    [InlineData("exponential", new[] { 100, 200, 400 })]
    public async Task Each_backoff_strategy_drives_its_delay_sequence_through_the_clock(string backoff, int[] expected)
    {
        var clock = new RecordingDelayClock();
        var (outcome, _) = await Runner.RunWithFakeAsync(
            RetryPayload($$"""{ "maxAttempts": 4, "delayMs": 100, "backoff": "{{backoff}}", "retryOn": ["timeout"] }"""),
            Inputs("inject-timeout-persist"),
            clock);

        Fail(outcome).Class.ShouldBe("retryable-exhausted"); // 8 scripted timeouts outlast the 4 attempts
        clock.Delays.ShouldBe(expected);
    }

    [Fact]
    public async Task Absent_backoff_behaves_exactly_like_constant()
    {
        // Backward compatibility: a retry block with no `backoff` waits delayMs before every retry, unchanged from before
        // the feature. inject-timeout clears after 2, so attempts 1 and 2 each back off 100 ms, then attempt 3 succeeds.
        var clock = new RecordingDelayClock();
        var (outcome, _) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 3, "delayMs": 100, "retryOn": ["timeout"] }"""),
            Inputs("inject-timeout"),
            clock);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        clock.Delays.ShouldBe([100, 100]); // the historical constant delay
    }

    [Fact]
    public async Task Max_delay_ms_caps_the_backoff_growth()
    {
        // exponential would be 100, 200, 400 — but maxDelayMs 250 saturates the third gap.
        var clock = new RecordingDelayClock();
        var (outcome, _) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 4, "delayMs": 100, "backoff": "exponential", "maxDelayMs": 250, "retryOn": ["timeout"] }"""),
            Inputs("inject-timeout-persist"),
            clock);

        Fail(outcome).Class.ShouldBe("retryable-exhausted");
        clock.Delays.ShouldBe([100, 200, 250]); // 400 → capped at 250
    }

    [Fact]
    public async Task Jitter_keeps_every_backoff_strictly_below_its_computed_ceiling()
    {
        // With full jitter each wait is a uniform draw in [0, ceiling) — so every recorded delay is < its exponential
        // ceiling (the un-jittered schedule would top out AT 4000, distinguishing the two). Deterministic bounds; the
        // exact-sample math is asserted in RetryBackoffTests.
        var clock = new RecordingDelayClock();
        var (outcome, _) = await Runner.RunWithFakeAsync(
            RetryPayload("""{ "maxAttempts": 4, "delayMs": 1000, "backoff": "exponential", "jitter": true, "retryOn": ["timeout"] }"""),
            Inputs("inject-timeout-persist"),
            clock);

        Fail(outcome).Class.ShouldBe("retryable-exhausted");
        clock.Delays.ShouldAllBe(d => d >= 0 && d < 4000); // full jitter ⇒ strictly under the top ceiling the un-jittered run would hit
    }

    [Fact]
    public async Task A_backoff_that_the_deadline_elapses_under_is_cut_short_terminally()
    {
        // Deadline interaction: the interpreter threads the run's deadline token straight into the backoff's Task.Delay,
        // so a wait the deadline breaches under throws (terminal), exactly like the connect-retry backoff. The clock fires
        // the deadline the instant the first (60 s) backoff is requested; the wait observes it and the run is not retried.
        using var cts = new CancellationTokenSource();
        var clock = new CancelOnDelayClock(cts);

        await Should.ThrowAsync<OperationCanceledException>(
            () => Runner.RunWithFakeAsync(
                RetryPayload("""{ "maxAttempts": 3, "delayMs": 60000, "backoff": "exponential", "retryOn": ["timeout"] }"""),
                Inputs("inject-timeout"),
                clock,
                ct: cts.Token));
    }

    [Fact]
    public async Task An_unknown_backoff_strategy_is_a_terminal_failure_on_an_inline_run()
    {
        // An inline run skips the schema (which rejects the enum at save/validate time), so an unrecognised strategy is
        // classified terminally rather than silently applying a constant delay it never asked for.
        var outcome = await Runner.RunAsync(
            RetryPayload("""{ "maxAttempts": 3, "delayMs": 0, "backoff": "fibonacci", "retryOn": ["timeout"] }"""),
            Inputs("inject-timeout"));

        var failure = Fail(outcome);
        failure.Class.ShouldBe("terminal");
        failure.Code.ShouldBe("invalid_retry_backoff");
        outcome.Events.OfType<RunAttemptFailed>().ShouldBeEmpty(); // rejected before the first attempt ran
    }

    [Fact]
    public async Task Close_quietly_tolerates_a_crashed_pages_close_failure()
    {
        // The reopen closes the crashed page best-effort; a real adapter's crashed page can throw on close.
        await Should.NotThrowAsync(() => RunInterpreter.CloseQuietlyAsync(new ThrowOnClosePage(), CancellationToken.None));
    }

    // Fires the run's deadline the instant the first backoff wait is requested, so the wait observes cancellation — the
    // deadline breaching mid-backoff. The interpreter threads the run's deadline token into Task.Delay, so the already
    // cancelled token completes the wait as OperationCanceledException without the (60 s) timer ever elapsing.
    private sealed class CancelOnDelayClock(CancellationTokenSource cts) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => FakeClock.Fixed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            cts.Cancel();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    // A page whose close fails with a browser fault (as a crashed Playwright page can), for the tolerate path above.
    private sealed class ThrowOnClosePage : IPageHandle
    {
        public string Url => string.Empty;

        public Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct) => Task.CompletedTask;

        public Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct) => Task.CompletedTask;

        public ILocatorHandle Locator(string selector) => throw new NotSupportedException();

        public ILocatorHandle GetByTitle(string title) => throw new NotSupportedException();

        public ILocatorHandle GetByRole(string role, string? name) => throw new NotSupportedException();

        public ILocatorHandle GetByText(string text) => throw new NotSupportedException();

        public IFrameHandle FrameLocator(string selector) => throw new NotSupportedException();

        public Task AddStyleTagAsync(string content, CancellationToken ct) => Task.CompletedTask;

        public Task RunAndWaitForRequestAsync(Func<Task> trigger, string urlPrefix, string? method, int? timeoutMs, CancellationToken ct) => Task.CompletedTask;

        public Task<IDownloadHandle> RunAndWaitForDownloadAsync(Func<Task> trigger, int? timeoutMs, CancellationToken ct) => throw new NotSupportedException();

        public Task CloseAsync(CancellationToken ct) => throw new BrowserPageCrashedException("cannot close a crashed page");

        public Task<byte[]> ScreenshotAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<string> ContentAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
