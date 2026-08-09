using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Tests.Unit;

/// <summary>
/// Screenshot-on-failure (§13): on a step failure the interpreter captures the page to blob storage and links the ref from
/// <c>StepFailed</c> — unless disabled via <c>config.screenshotOnFailure</c>, there is no page (a setup failure), or the
/// capture itself fails (a crashed page — tolerated, so it never masks the run's own failure). The event stores only the
/// ref, never the image (§12).
/// </summary>
public class RunScreenshotTests
{
    private const string _fail =
        """
        { "name": "t", "config": { "backend": "input.backend"SCREENSHOT }, "vars": {},
          "steps": [ { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx" } },
                     { "fail": { "class": "terminal", "code": "boom", "message": "kaboom" } } ],
          "result": "'unreached'" }
        """;

    private static string FailPayload(bool screenshots = true) =>
        _fail.Replace("SCREENSHOT", screenshots ? "" : ", \"screenshotOnFailure\": false", StringComparison.Ordinal);

    private static StepFailed OnlyStepFailed(RecordingObserver observer) => observer.Events.OfType<StepFailed>().ShouldHaveSingleItem();

    [Fact]
    public async Task A_step_failure_captures_a_screenshot_and_links_its_ref()
    {
        var (outcome, observer, screenshots) = await Runner.RunWithObserverAsync(FailPayload());

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe("boom");

        var stepFailed = OnlyStepFailed(observer);
        stepFailed.Error.ShouldBe("boom");
        stepFailed.ScreenshotRef.ShouldNotBeNull();

        // The ref is a content-addressed key (credential-free, §12), and the store holds the captured PNG bytes.
        stepFailed.ScreenshotRef.ShouldStartWith("screenshots/");
        screenshots.Blobs.Keys.ShouldContain(stepFailed.ScreenshotRef);
        screenshots.Blobs[stepFailed.ScreenshotRef][..8].ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature
    }

    [Fact]
    public async Task Screenshot_on_failure_disabled_captures_nothing()
    {
        var (outcome, observer, screenshots) = await Runner.RunWithObserverAsync(FailPayload(screenshots: false));

        outcome.Status.ShouldBe(RunStatus.Failed);
        OnlyStepFailed(observer).ScreenshotRef.ShouldBeNull();
        screenshots.Blobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_setup_failure_before_a_page_is_bound_captures_no_screenshot()
    {
        // config.backend resolves to a non-object → invalid_backend_binding, before any page — StepFailed carries a null ref.
        const string Payload = """{ "name": "t", "config": { "backend": "42" }, "vars": {}, "steps": [], "result": "'ok'" }""";
        var (outcome, observer, screenshots) = await Runner.RunWithObserverAsync(Payload);

        outcome.Status.ShouldBe(RunStatus.Failed);
        var stepFailed = OnlyStepFailed(observer);
        stepFailed.Error.ShouldBe("invalid_backend_binding");
        stepFailed.ScreenshotRef.ShouldBeNull();
        screenshots.Blobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_screenshot_that_fails_is_tolerated_and_does_not_mask_the_failure()
    {
        // A crashed page can fail to screenshot; the capture is best-effort, so StepFailed still fires (ref null) and the
        // run's own failure is reported unchanged.
        var (outcome, observer, screenshots) = await Runner.RunWithObserverAsync(
            FailPayload(), backend: new ScreenshotFailingBackend(Runner.FixturesRoot));

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe("boom");
        OnlyStepFailed(observer).ScreenshotRef.ShouldBeNull();
        screenshots.Blobs.ShouldBeEmpty();
    }

    // A backend whose pages throw a browser fault on screenshot capture (a crashed page), decorating the record/replay fake.
    private sealed class ScreenshotFailingBackend(string root) : IBrowserBackend
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

            public Task<byte[]> ScreenshotAsync(CancellationToken ct) => throw new BrowserPageCrashedException("cannot screenshot a crashed page");
        }
    }
}
