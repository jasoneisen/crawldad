using Crawldad.Api.Features.Runs;
using Crawldad.Contracts.Runs;

namespace Crawldad.Api.Features.Drift;

/// <summary>One completed canary observation of a payload — the drift-relevant projection of a run's
/// <see cref="RunTimeline"/>: which extraction selectors missed, plus the run's identity, disposition, time, pinned
/// revision, and evidence refs. The drift monitor folds a payload's stream of these under a baseline/delta model
/// (<see cref="DriftAnalysis"/>). Refs only — never bytes or page content.</summary>
public sealed record DriftObservation(
    Guid RunId,
    RunStatus Status,
    DateTimeOffset ObservedAt,
    int? PayloadRevision,
    IReadOnlyList<string> MissedSelectors,
    string? FailureScreenshotRef,
    IReadOnlyList<string> CaptureRefs,
    IReadOnlyList<string> ScreenshotRefs)
{
    /// <summary>Projects a run's timeline into its drift observation. <see cref="ObservedAt"/> is the run's finish
    /// instant (its start while still running — never the case for a completed observation the monitor selects); the
    /// failure screenshot is the failing-step ref carried into the terminal failure, else the timeline's own step ref.</summary>
    public static DriftObservation FromTimeline(RunTimeline timeline) => new(
        timeline.Id,
        timeline.Status,
        timeline.FinishedAt ?? timeline.StartedAt,
        timeline.PayloadRevision,
        timeline.MissedSelectors,
        timeline.Failure?.ScreenshotRef ?? timeline.ScreenshotRef,
        [.. timeline.Captures.Select(capture => capture.BlobRef)],
        [.. timeline.Screenshots.Select(shot => shot.ScreenshotRef)]);
}
