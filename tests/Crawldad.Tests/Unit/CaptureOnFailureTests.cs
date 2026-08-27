using System.Text;
using System.Text.Json.Nodes;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Browser.Fake;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary><c>config.captureOnFailure</c>: on a step failure the interpreter serialises the failing page's full HTML to
/// the tenant BYO sink and records a <c>Captured</c> event with only its ref — next to the failure screenshot, the
/// clearest signal of selector drift. Absent ⇒ disabled; a bad target fails at setup; a crashed page's capture is
/// tolerated; and the synchronous path (no observer) captures nothing, exactly like the failure screenshot.</summary>
public class CaptureOnFailureTests
{
    private const string _captureInputs =
        """{ "backend": { "adapter": "fake", "options": { "fixture": "capture-sample" } } }""";

    // The captureOnFailure config fragment: streams the failing page's HTML to the fake sink (kind "fake") the harness binds.
    private const string _enabled = """, "captureOnFailure": { "to": "{ kind: 'fake', name: 'x' }" }""";

    // A run that navigates to the fixture page, then fails terminally — a page is bound, so the failure-capture path runs.
    private static string FailPayload(string captureOnFailure = "") =>
        $$"""
        { "name": "t", "config": { "backend": "input.backend"{{captureOnFailure}} }, "vars": {},
          "steps": [ { "goto": { "url": "https://fixture.test/parcel/42" } },
                     { "fail": { "class": "terminal", "code": "boom", "message": "kaboom" } } ],
          "result": "'unreached'" }
        """;

    [Fact]
    public async Task A_step_failure_captures_the_page_html_to_byo_storage_next_to_the_screenshot()
    {
        var sink = new FakeDownloadSink();
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(FailPayload(_enabled), _captureInputs, sink: sink);

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe("boom");

        // The failing page's full HTML streamed to the BYO sink; a Captured event records only its (content-addressed) ref.
        var captured = observer.Events.OfType<Captured>().ShouldHaveSingleItem();
        captured.BlobRef.ShouldBe($"{sink.Stored.ShouldHaveSingleItem()}.html");
        captured.Sha256.Length.ShouldBe(64);

        var html = Encoding.UTF8.GetString(sink.BytesOf(sink.Stored.First()));
        html.ShouldContain("Parcel 42");
        html.ShouldContain("token=abc123SECRETtoken"); // the failing page is byte-faithful too — the scrubber never runs on it

        // Next to the screenshot: StepFailed carries the failure-screenshot ref AND an explicit ref to the captured HTML
        // doc (issue #101), so the failure links its captured page by ref rather than by position in captures[].
        var stepFailed = observer.Events.OfType<StepFailed>().ShouldHaveSingleItem();
        stepFailed.ScreenshotRef.ShouldNotBeNull();
        stepFailed.CaptureRef.ShouldBe(captured.BlobRef); // points at the exact document the Captured event banked
    }

    [Fact]
    public async Task Capture_on_failure_absent_captures_nothing()
    {
        var sink = new FakeDownloadSink();
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(FailPayload(), _captureInputs, sink: sink);

        outcome.Status.ShouldBe(RunStatus.Failed);
        observer.Events.OfType<Captured>().ShouldBeEmpty();
        sink.Stored.ShouldBeEmpty();
        observer.Events.OfType<StepFailed>().ShouldHaveSingleItem().CaptureRef.ShouldBeNull(); // nothing captured ⇒ no ref to link
    }

    [Fact]
    public async Task A_failing_capture_is_tolerated_and_does_not_mask_the_run_failure()
    {
        // A crashed page can fail to serialise; the capture is best-effort, so the run's own failure is reported unchanged
        // and no Captured event lands — exactly like a failed failure-screenshot.
        var sink = new FakeDownloadSink();
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(
            FailPayload(_enabled), _captureInputs, sink: sink, backend: new ContentFailingBackend(Runner.FixturesRoot));

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe("boom");
        observer.Events.OfType<Captured>().ShouldBeEmpty();
        sink.Stored.ShouldBeEmpty();
        observer.Events.OfType<StepFailed>().ShouldHaveSingleItem().CaptureRef.ShouldBeNull(); // a tolerated capture failure links no ref
    }

    [Fact]
    public async Task The_synchronous_path_captures_no_failure_html() // no observer ⇒ capture-on-failure is inert, like the failure screenshot
    {
        var sink = new FakeDownloadSink();
        var outcome = await Runner.RunWithSinkAsync(FailPayload(_enabled), _captureInputs, sink);

        outcome.Status.ShouldBe(RunStatus.Failed);
        sink.Stored.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("'not a target'", InterpreterErrorCodes.InvalidCaptureTarget)]
    [InlineData("{ kind: 'nope' }", InterpreterErrorCodes.UnknownCaptureSink)]
    public async Task A_bad_capture_on_failure_target_fails_the_run_at_setup(string toExpr, string code)
    {
        // captureOnFailure.to is resolved up front (like config.backend), so a bad target is a terminal setup failure —
        // before any step, with no page and therefore no screenshot.
        var (outcome, _) = await Runner.RunWithFakeAsync(BadTargetPayload(toExpr), _captureInputs);

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe(code);
    }

    private static string BadTargetPayload(string toExpr)
    {
        var payload = new JsonObject
        {
            ["name"] = "t",
            ["config"] = new JsonObject
            {
                ["backend"] = "input.backend",
                ["captureOnFailure"] = new JsonObject { ["to"] = toExpr },
            },
            ["vars"] = new JsonObject(),
            ["steps"] = new JsonArray(),
            ["result"] = "'ok'",
        };
        return payload.ToJsonString();
    }

    // A backend whose pages throw a browser fault on ContentAsync (a crashed page failing to serialise), decorating the
    // record/replay fake — everything else (goto, the failure screenshot) still works, isolating the capture failure.
    private sealed class ContentFailingBackend(string root) : IBrowserBackend
    {
        private readonly FakeBrowserBackend _inner = new(root);

        public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct) =>
            new Session(await _inner.ConnectAsync(binding, policy, ct));

        private sealed class Session(IBrowserSession inner) : IBrowserSession
        {
            public string Region => inner.Region;

            public int CacheHits => inner.CacheHits;

            public async Task<IPageHandle> NewPageAsync(CancellationToken ct) => new Page(await inner.NewPageAsync(ct));

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class Page(IPageHandle inner) : IPageHandle
        {
            public string Url => inner.Url;

            public Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct) => inner.GotoAsync(url, waitUntil, timeoutMs, ct);

            public Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct) => inner.WaitForLoadStateAsync(state, timeoutMs, ct);

            public ILocatorHandle Locator(string selector) => inner.Locator(selector);

            public ILocatorHandle GetByTitle(string title) => inner.GetByTitle(title);

            public ILocatorHandle GetByRole(string role, string? name) => inner.GetByRole(role, name);

            public ILocatorHandle GetByText(string text) => inner.GetByText(text);

            public IFrameHandle FrameLocator(string selector) => inner.FrameLocator(selector);

            public Task AddStyleTagAsync(string content, CancellationToken ct) => inner.AddStyleTagAsync(content, ct);

            public Task RunAndWaitForRequestAsync(Func<Task> trigger, string urlPrefix, string? method, int? timeoutMs, CancellationToken ct) =>
                inner.RunAndWaitForRequestAsync(trigger, urlPrefix, method, timeoutMs, ct);

            public Task<IDownloadHandle> RunAndWaitForDownloadAsync(Func<Task> trigger, int? timeoutMs, CancellationToken ct) =>
                inner.RunAndWaitForDownloadAsync(trigger, timeoutMs, ct);

            public Task CloseAsync(CancellationToken ct) => inner.CloseAsync(ct);

            public Task<byte[]> ScreenshotAsync(CancellationToken ct) => inner.ScreenshotAsync(ct);

            public Task<string> ContentAsync(CancellationToken ct) => throw new BrowserPageCrashedException("cannot serialise a crashed page");
        }
    }
}
