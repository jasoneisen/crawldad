using Crawldad.Api.Features.Drift;
using Crawldad.Api.Features.Runs;
using Crawldad.Contracts.Drift;
using Crawldad.Contracts.Runs;

namespace Crawldad.Tests.Unit;

/// <summary>The baseline/delta drift fold (issue #47): <see cref="DriftAnalysis.Analyze"/> classifies a payload from its
/// earliest healthy observations (the miss floor) versus its latest completed run. A selector missing since the baseline
/// was established is the steady-state floor (a legitimate multi-selector fallback), never drift; a selector that matched
/// at baseline and is newly missing is drift, subject to the per-payload threshold. Exercised as a pure function, no DB.</summary>
public class DriftAnalysisTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _payloadId = Guid.NewGuid();

    private static DriftObservation Obs(
        RunStatus status,
        int minute,
        string[] missed,
        int? revision = 4,
        string? failScreenshot = null,
        string[]? captures = null,
        string[]? screenshots = null) =>
        new(Guid.NewGuid(), status, _t0.AddMinutes(minute), revision, missed, failScreenshot, captures ?? [], screenshots ?? []);

    private static PayloadDriftStatus Analyze(IReadOnlyList<DriftObservation> baseline, DriftObservation? current, int observedRuns, int threshold = 0) =>
        DriftAnalysis.Analyze(_payloadId, "ljcmg.canary", baseline, current, observedRuns, DriftAnalysis.DefaultBaselineRuns, threshold);

    [Fact]
    public void No_observations_is_no_data()
    {
        var status = Analyze([], current: null, observedRuns: 0);

        status.State.ShouldBe(DriftState.NoData);
        status.Drifted.ShouldBeFalse();
        status.PayloadId.ShouldBe(_payloadId);
        status.PayloadName.ShouldBe("ljcmg.canary");
        status.ObservedRuns.ShouldBe(0);
        status.BaselineRuns.ShouldBe(DriftAnalysis.DefaultBaselineRuns);
        status.DriftedSelectorCount.ShouldBe(0);
        status.Selectors.ShouldBeEmpty();
        status.Evidence.ShouldBeNull();
        status.FirstObservedAt.ShouldBeNull();
        status.LastObservedAt.ShouldBeNull();
        status.PinnedRevision.ShouldBeNull();
    }

    [Fact]
    public void Completed_runs_but_no_healthy_baseline_is_warming_up()
    {
        // Every completed run so far failed (e.g. the site was already down when the canary began) — there is no healthy
        // run to baseline against, so nothing is alarmed and the timestamps fall back to the latest observation.
        var failed = Obs(RunStatus.Failed, 5, ["#a"]);
        var status = Analyze([], failed, observedRuns: 2);

        status.State.ShouldBe(DriftState.WarmingUp);
        status.Drifted.ShouldBeFalse();
        status.Selectors.ShouldBeEmpty();
        status.DriftedSelectorCount.ShouldBe(0);
        status.FirstObservedAt.ShouldBe(failed.ObservedAt);
        status.LastObservedAt.ShouldBe(failed.ObservedAt);
        status.Evidence.ShouldNotBeNull();
        status.Evidence!.RunId.ShouldBe(failed.RunId);
    }

    [Fact]
    public void Latest_run_still_inside_the_baseline_window_is_warming_up()
    {
        // The baseline window is not yet closed: the latest completed run is itself one of the baseline runs, so there is
        // no post-baseline observation to compare against.
        var r1 = Obs(RunStatus.Succeeded, 1, []);
        var r2 = Obs(RunStatus.Succeeded, 2, ["#a"]);
        var status = Analyze([r1, r2], current: r2, observedRuns: 2);

        status.State.ShouldBe(DriftState.WarmingUp);
        status.Selectors.ShouldBeEmpty();
        status.FirstObservedAt.ShouldBe(r1.ObservedAt);
    }

    [Fact]
    public void A_clean_latest_run_after_the_baseline_is_steady()
    {
        var r1 = Obs(RunStatus.Succeeded, 1, []);
        var r2 = Obs(RunStatus.Succeeded, 2, []);
        var latest = Obs(RunStatus.Succeeded, 9, []);
        var status = Analyze([r1, r2], latest, observedRuns: 3);

        status.State.ShouldBe(DriftState.Steady);
        status.Drifted.ShouldBeFalse();
        status.Selectors.ShouldBeEmpty();
        status.DriftedSelectorCount.ShouldBe(0);
        status.PinnedRevision.ShouldBe(4);
    }

    [Fact]
    public void A_selector_missing_since_the_baseline_is_the_floor_not_drift()
    {
        // "#fallback" missed in the baseline (a legit coalesce fallback branch) AND misses now — its continued miss is
        // expected, so the payload stays steady and the selector is reported as baseline floor, not drift.
        var baseline = new[] { Obs(RunStatus.Succeeded, 1, ["#fallback"]), Obs(RunStatus.Succeeded, 2, ["#fallback"]) };
        var latest = Obs(RunStatus.Succeeded, 9, ["#fallback"]);
        var status = Analyze(baseline, latest, observedRuns: 3);

        status.State.ShouldBe(DriftState.Steady);
        status.DriftedSelectorCount.ShouldBe(0);
        var detail = status.Selectors.ShouldHaveSingleItem();
        detail.Selector.ShouldBe("#fallback");
        detail.MissingInLatest.ShouldBeTrue();
        detail.BaselineFloor.ShouldBeTrue();
        detail.Drifted.ShouldBeFalse();
    }

    [Fact]
    public void A_selector_that_matched_at_baseline_and_is_newly_missing_is_drift()
    {
        // "#fallback" is floor (missed at baseline); "#title" matched at baseline and is newly missing → drift. The
        // detail list is ordinal-sorted and separates the two.
        var baseline = new[] { Obs(RunStatus.Succeeded, 1, ["#fallback"]), Obs(RunStatus.Succeeded, 2, ["#fallback"]) };
        var latest = Obs(RunStatus.Failed, 9, ["#title", "#fallback"], failScreenshot: "screenshots/boom.png", captures: ["captures/page.html"], screenshots: ["screenshots/x.png"]);
        var status = Analyze(baseline, latest, observedRuns: 3);

        status.State.ShouldBe(DriftState.Drifted);
        status.Drifted.ShouldBeTrue();
        status.DriftedSelectorCount.ShouldBe(1);

        status.Selectors.Select(s => s.Selector).ShouldBe(["#fallback", "#title"]); // ordinal-sorted
        var fallback = status.Selectors.Single(s => string.Equals(s.Selector, "#fallback", StringComparison.Ordinal));
        fallback.Drifted.ShouldBeFalse();
        fallback.BaselineFloor.ShouldBeTrue();
        var title = status.Selectors.Single(s => string.Equals(s.Selector, "#title", StringComparison.Ordinal));
        title.Drifted.ShouldBeTrue();
        title.BaselineFloor.ShouldBeFalse();
        title.MissingInLatest.ShouldBeTrue();

        // Evidence rides along: the latest run's identity, status, and refs so the alert arrives with the changed page.
        status.Evidence.ShouldNotBeNull();
        status.Evidence!.RunId.ShouldBe(latest.RunId);
        status.Evidence.Status.ShouldBe(RunStatus.Failed);
        status.Evidence.FailureScreenshotRef.ShouldBe("screenshots/boom.png");
        status.Evidence.CaptureRefs.ShouldBe(["captures/page.html"]);
        status.Evidence.ScreenshotRefs.ShouldBe(["screenshots/x.png"]);
    }

    [Fact]
    public void The_threshold_tolerates_new_misses_up_to_its_count()
    {
        // One new miss with threshold 1 → not drifted (count is not ABOVE the threshold); the detail still marks it drift.
        var baseline = new[] { Obs(RunStatus.Succeeded, 1, []), Obs(RunStatus.Succeeded, 2, []) };
        var latest = Obs(RunStatus.Succeeded, 9, ["#title"]);

        var tolerated = Analyze(baseline, latest, observedRuns: 3, threshold: 1);
        tolerated.State.ShouldBe(DriftState.Steady);
        tolerated.Drifted.ShouldBeFalse();
        tolerated.DriftedSelectorCount.ShouldBe(1);
        tolerated.Threshold.ShouldBe(1);
        tolerated.Selectors.ShouldHaveSingleItem().Drifted.ShouldBeTrue();

        // The same observations with the default threshold 0 → drifted.
        Analyze(baseline, latest, observedRuns: 3, threshold: 0).State.ShouldBe(DriftState.Drifted);
    }

    [Fact]
    public void Repeated_missed_selectors_in_a_run_are_deduped_in_the_detail()
    {
        var baseline = new[] { Obs(RunStatus.Succeeded, 1, []), Obs(RunStatus.Succeeded, 2, []) };
        var latest = Obs(RunStatus.Succeeded, 9, ["#dup", "#dup"]);
        var status = Analyze(baseline, latest, observedRuns: 3);

        status.Selectors.ShouldHaveSingleItem().Selector.ShouldBe("#dup");
        status.DriftedSelectorCount.ShouldBe(1);
    }

    [Fact]
    public void Analyze_rejects_a_null_baseline() =>
        Should.Throw<ArgumentNullException>(() => DriftAnalysis.Analyze(_payloadId, "x", null!, null, 0, 3, 0));

    [Fact]
    public void FromTimeline_projects_a_succeeded_run_with_capture_and_screenshot_refs()
    {
        var timeline = new RunTimeline
        {
            Id = Guid.NewGuid(),
            Status = RunStatus.Succeeded,
            PayloadRevision = 7,
            StartedAt = _t0,
            FinishedAt = _t0.AddSeconds(3),
            MissedSelectors = ["#a", "#b"],
            Captures = [new RunTimelineCapture("captures/one.html", 10, "sha-a")],
            Screenshots = [new RunTimelineScreenshot("screenshots/one.png", "shot", 20)],
        };

        var observation = DriftObservation.FromTimeline(timeline);

        observation.RunId.ShouldBe(timeline.Id);
        observation.Status.ShouldBe(RunStatus.Succeeded);
        observation.ObservedAt.ShouldBe(_t0.AddSeconds(3)); // finished instant
        observation.PayloadRevision.ShouldBe(7);
        observation.MissedSelectors.ShouldBe(["#a", "#b"]);
        observation.FailureScreenshotRef.ShouldBeNull(); // no failure
        observation.CaptureRefs.ShouldBe(["captures/one.html"]);
        observation.ScreenshotRefs.ShouldBe(["screenshots/one.png"]);
    }

    [Fact]
    public void FromTimeline_takes_the_failure_screenshot_ref_and_falls_back_to_start_time()
    {
        var timeline = new RunTimeline
        {
            Id = Guid.NewGuid(),
            Status = RunStatus.Failed,
            StartedAt = _t0,
            FinishedAt = null, // no finish recorded → ObservedAt falls back to StartedAt
            MissedSelectors = ["#title"],
            Failure = new RunTimelineFailure("selector_miss", "gone", new RunStepRef(1, "set"), "screenshots/fail.png", null),
        };

        var observation = DriftObservation.FromTimeline(timeline);

        observation.ObservedAt.ShouldBe(_t0);
        observation.FailureScreenshotRef.ShouldBe("screenshots/fail.png");
        observation.CaptureRefs.ShouldBeEmpty();
        observation.ScreenshotRefs.ShouldBeEmpty();
    }
}
