using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs;

namespace Crawldad.Tests.Unit;

/// <summary>The <see cref="RunTimelineProjection"/> fold: opens on <c>RunStarted</c>, records region/steps/extracts/downloads
/// from the step trace, closes each step's duration when the next step (or the terminal event) lands, and attaches the
/// failure + screenshot ref. Exercised by folding events directly, without a database.</summary>
public class RunTimelineProjectionTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static RunStarted Started(Guid? payloadId = null, int? revision = null) =>
        new("ljcmg.search", "hash-abc", _t0, ["backend", "startDate"], payloadId, revision);

    [Fact]
    public void A_successful_run_folds_steps_with_durations_extracts_downloads_and_region()
    {
        var projection = new RunTimelineProjection();
        var payloadId = Guid.NewGuid();

        var t = projection.Create(Started(payloadId, 4));
        t = projection.Apply(new RunSessionOpened("us-east-1", _t0), t);
        t = projection.Apply(new StepStarted(0, "goto", _t0), t);
        t = projection.Apply(new Extracted("rows", "list(3)", _t0), t);
        t = projection.Apply(new Extracted("more", "map(1)", _t0), t);            // a second extract → non-empty spread
        t = projection.Apply(new Downloaded("abc.pdf", "application/pdf", 30, "sha", _t0), t);
        t = projection.Apply(new Downloaded("def.csv", "text/csv", 12, "sha2", _t0), t); // a second download → non-empty spread
        t = projection.Apply(new StepStarted(1, "loop", _t0.AddSeconds(1)), t); // closes step 0 → 1000ms
        t = projection.Apply(new RunSucceeded(new RunStats(0, 0, 0, 0, 0), _t0.AddSeconds(3)), t);

        t.PayloadName.ShouldBe("ljcmg.search");
        t.ScriptHash.ShouldBe("hash-abc");
        t.PayloadId.ShouldBe(payloadId);
        t.PayloadRevision.ShouldBe(4);
        t.InputKeys.ShouldBe(["backend", "startDate"]);
        t.Region.ShouldBe("us-east-1");
        t.Status.ShouldBe(RunStatus.Succeeded);
        t.DurationMs.ShouldBe(3000);

        t.Steps.Count.ShouldBe(2);
        t.Steps[0].ShouldBe(new RunTimelineStep(0, "goto", _t0, 1000));       // closed by the next step
        t.Steps[1].ShouldBe(new RunTimelineStep(1, "loop", _t0.AddSeconds(1), 2000)); // closed by the terminal event
        t.Extracted.ShouldBe([new RunTimelineExtract("rows", "list(3)"), new RunTimelineExtract("more", "map(1)")]);
        t.Downloads.ShouldBe([new RunTimelineDownload("abc.pdf", "application/pdf", 30, "sha"), new RunTimelineDownload("def.csv", "text/csv", 12, "sha2")]);
        t.Failure.ShouldBeNull();
    }

    [Fact]
    public void A_run_folds_explicit_screenshots_in_capture_order() // author-requested artifacts, curated like downloads
    {
        var projection = new RunTimelineProjection();

        var t = projection.Create(Started());
        t = projection.Apply(new StepStarted(0, "screenshot", _t0), t);
        t = projection.Apply(new Screenshotted("screenshots/aaa.png", "after-login", 2048, _t0), t);
        t = projection.Apply(new Screenshotted("screenshots/bbb.png", null, 4096, _t0), t); // a second, unnamed → non-empty spread
        t = projection.Apply(new RunSucceeded(new RunStats(0, 0, 0, 0, 0), _t0.AddSeconds(1)), t);

        t.Screenshots.ShouldBe([
            new RunTimelineScreenshot("screenshots/aaa.png", "after-login", 2048),
            new RunTimelineScreenshot("screenshots/bbb.png", null, 4096),
        ]);
    }

    [Fact]
    public void A_failed_run_carries_the_step_failure_and_its_screenshot_ref()
    {
        var projection = new RunTimelineProjection();
        var failure = new RunFailureDetail("terminal", "boom", "kaboom", new RunStepRef(1, "fail"));

        var t = projection.Create(Started());
        t = projection.Apply(new StepStarted(0, "goto", _t0), t);
        t = projection.Apply(new StepStarted(1, "fail", _t0), t);
        t = projection.Apply(new StepFailed(1, "boom", "screenshots/xyz.png", _t0), t);
        t = projection.Apply(new RunFailed(failure, new RunStats(0, 0, 0, 0, 0), _t0.AddSeconds(2)), t);

        t.Status.ShouldBe(RunStatus.Failed);
        t.Failure.ShouldBe(new RunTimelineFailure("boom", "kaboom", new RunStepRef(1, "fail"), "screenshots/xyz.png"));
    }

    [Fact]
    public void A_setup_failure_with_no_steps_folds_a_stepless_failed_timeline()
    {
        // A run that fails before any StepStarted (a setup failure) has nothing to close — CloseLastStep's empty branch.
        var projection = new RunTimelineProjection();
        var failure = new RunFailureDetail("terminal", "invalid_backend_binding", "bad", new RunStepRef(0, "config"));

        var t = projection.Create(Started());
        t = projection.Apply(new StepFailed(0, "invalid_backend_binding", null, _t0), t);
        t = projection.Apply(new RunFailed(failure, new RunStats(0, 0, 0, 0, 0), _t0.AddSeconds(1)), t);

        t.Steps.ShouldBeEmpty();
        t.Status.ShouldBe(RunStatus.Failed);
        t.Failure!.ScreenshotRef.ShouldBeNull();
    }

    [Fact]
    public void A_cancelled_run_folds_a_cancelled_timeline_with_no_failure()
    {
        var projection = new RunTimelineProjection();

        var t = projection.Create(Started());
        t = projection.Apply(new StepStarted(0, "loop", _t0), t);
        t = projection.Apply(new RunCancelled(new RunStats(0, 0, 0, 0, 0), _t0.AddSeconds(5)), t);

        t.Status.ShouldBe(RunStatus.Cancelled);
        t.Steps.ShouldHaveSingleItem().DurationMs.ShouldBe(5000);
        t.Failure.ShouldBeNull();
    }
}
