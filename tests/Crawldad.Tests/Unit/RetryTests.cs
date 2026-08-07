using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The retry/resilience layer (§8.3): the retryable-vs-terminal classifier, the <c>retryOn</c> gate, retryable
/// exhaustion, and the §3.6 page-crash reopen-and-rebind. Faults are scripted by the <c>inject-timeout</c>/
/// <c>inject-crash</c> fixtures (§ Deliverable 3); delays run through the frozen clock at 0ms (one real-clock case
/// proves the delay path) so the suite stays deterministic and fast.
/// </summary>
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

    [Fact]
    public async Task A_terminal_guard_failure_is_never_retried()
    {
        // Retry is configured generously, but a terminal guard aborts on the first attempt (the ~30-min lesson, §8.3).
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

    [Fact]
    public async Task Close_quietly_tolerates_a_crashed_pages_close_failure()
    {
        // The §3.6 reopen closes the crashed page best-effort; a real adapter's crashed page can throw on close.
        await Should.NotThrowAsync(() => RunInterpreter.CloseQuietlyAsync(new ThrowOnClosePage(), CancellationToken.None));
    }

    // A page whose close fails with a browser fault (as a crashed Playwright page can), for the tolerate path above.
    private sealed class ThrowOnClosePage : IPageHandle
    {
        public string Url => string.Empty;

        public Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct) => Task.CompletedTask;

        public Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct) => Task.CompletedTask;

        public ILocatorHandle Locator(string selector) => throw new NotSupportedException();

        public ILocatorHandle GetByTitle(string title) => throw new NotSupportedException();

        public IFrameHandle FrameLocator(string selector) => throw new NotSupportedException();

        public Task AddStyleTagAsync(string content, CancellationToken ct) => Task.CompletedTask;

        public Task RunAndWaitForRequestAsync(Func<Task> trigger, string urlPrefix, string? method, int? timeoutMs, CancellationToken ct) => Task.CompletedTask;

        public Task<IDownloadHandle> RunAndWaitForDownloadAsync(Func<Task> trigger, int? timeoutMs, CancellationToken ct) => throw new NotSupportedException();

        public Task CloseAsync(CancellationToken ct) => throw new BrowserPageCrashedException("cannot close a crashed page");

        public Task<byte[]> ScreenshotAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
