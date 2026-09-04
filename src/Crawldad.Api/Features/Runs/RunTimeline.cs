using Crawldad.Contracts.Runs;
using Marten.Events.Aggregation;

namespace Crawldad.Api.Features.Runs;

/// <summary>The async run-observability read model: one document per run, folded from its step-trace into the ordered
/// step list, artifact refs, and failure. Distinct from the read-your-writes <see cref="RunProgress"/> — this is the
/// lag-tolerant cross-run dashboard view, exposed as <see cref="RunTimelineResponse"/>, never this document directly.</summary>
public sealed record RunTimeline
{
    /// <summary>The run id (the event-stream id; assigned by Marten from the stream).</summary>
    public Guid Id { get; init; }

    /// <summary>The payload name pinned at start.</summary>
    public string PayloadName { get; init; } = "";

    /// <summary>The script hash pinned at start (SHA-256, lowercase hex).</summary>
    public string ScriptHash { get; init; } = "";

    /// <summary>The pinned managed payload, or null for an inline run.</summary>
    public Guid? PayloadId { get; init; }

    /// <summary>The pinned payload revision, or null for an inline run.</summary>
    public int? PayloadRevision { get; init; }

    /// <summary>The supplied input key names (redacted — never values).</summary>
    public IReadOnlyList<string> InputKeys { get; init; } = [];

    /// <summary>The backend region the session ran in, or null before a session opened.</summary>
    public string? Region { get; init; }

    /// <summary>The run's disposition (running until a terminal event lands).</summary>
    public RunStatus Status { get; init; } = RunStatus.Running;

    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the run reached a terminal status, or null while still running.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The run's total wall-clock duration, or null while still running.</summary>
    public long? DurationMs { get; init; }

    /// <summary>The ordered top-level step list with per-step durations.</summary>
    public IReadOnlyList<RunTimelineStep> Steps { get; init; } = [];

    /// <summary>The values bound into the run's data model (key + shape ref, never the value).</summary>
    public IReadOnlyList<RunTimelineExtract> Extracted { get; init; } = [];

    /// <summary>The downloads streamed to blob storage (refs + metadata, never bytes).</summary>
    public IReadOnlyList<RunTimelineDownload> Downloads { get; init; } = [];

    /// <summary>The explicit <c>screenshot</c> captures (refs + metadata, never images), in capture order.</summary>
    public IReadOnlyList<RunTimelineScreenshot> Screenshots { get; init; } = [];

    /// <summary>The documents a <c>capture</c> node (or capture-on-failure) streamed to tenant BYO storage (refs +
    /// metadata, never the HTML), in capture order.</summary>
    public IReadOnlyList<RunTimelineCapture> Captures { get; init; } = [];

    /// <summary>The distinct extraction selectors that matched no element in this run, in first-seen order — the
    /// per-run drift signal folded from <c>SelectorMiss</c> (issue #47). Deduped like the trace's own per-selector
    /// dedupe, so a selector that drifted across every row of a loop appears once. Populated on the durable executor
    /// path only (the sole path that emits the trace); the declared selector text, never page content.</summary>
    public IReadOnlyList<string> MissedSelectors { get; init; } = [];

    /// <summary>The failure screenshot's ref captured on the failing step, carried into <see cref="Failure"/> at the terminal event.</summary>
    public string? ScreenshotRef { get; init; }

    /// <summary>The failing page's <c>config.captureOnFailure</c> HTML document ref, carried into <see cref="Failure"/> at
    /// the terminal event — the same content-addressed ref that also appears in <see cref="Captures"/>, so the failure
    /// links its captured page explicitly. Null when capture-on-failure was disabled or captured nothing.</summary>
    public string? CaptureRef { get; init; }

    /// <summary>The terminal failure + its screenshot/capture refs, or null when the run did not fail.</summary>
    public RunTimelineFailure? Failure { get; init; }
}

/// <summary>Folds a run's trace events into its <see cref="RunTimeline"/>. Reacts only to the events it curates —
/// <c>StepStarted</c> spines the step list, <c>Extracted</c>/<c>Downloaded</c>/<c>Screenshotted</c>/<c>Captured</c> collect
/// artifacts, terminals close durations — and ignores the finer <c>Navigated</c>/<c>Clicked</c>/<c>Waited</c> events (those serve the live SSE tail).</summary>
public sealed class RunTimelineProjection : SingleStreamProjection<RunTimeline, Guid>
{
    /// <summary>Opens the timeline on the run's opening event (a run started immediately, under the cap).</summary>
    public RunTimeline Create(RunStarted started) => new()
    {
        PayloadName = started.PayloadName,
        ScriptHash = started.ScriptHash,
        PayloadId = started.PayloadId,
        PayloadRevision = started.PayloadRevision,
        InputKeys = [.. started.InputKeys],
        StartedAt = started.StartedAt,
    };

    /// <summary>Opens the timeline on the opening event of a run <b>queued</b> at the cap. <see cref="RunTimeline.StartedAt"/>
    /// is seeded to the enqueue instant so a run cancelled or expired while still queued has a sane baseline; a promoted
    /// run overwrites it with its real execution start at <see cref="Apply(RunDequeued, RunTimeline)"/>.</summary>
    public RunTimeline Create(RunQueued queued) => new()
    {
        PayloadName = queued.PayloadName,
        ScriptHash = queued.ScriptHash,
        PayloadId = queued.PayloadId,
        PayloadRevision = queued.PayloadRevision,
        InputKeys = [.. queued.InputKeys],
        StartedAt = queued.QueuedAt,
    };

    /// <summary>Stamps the real execution start when a queued run is promoted, so the timeline's duration measures
    /// execution — not the time spent waiting in the queue.</summary>
    public RunTimeline Apply(RunDequeued dequeued, RunTimeline timeline) => timeline with { StartedAt = dequeued.StartedAt };

    /// <summary>Records the backend region once the session opened.</summary>
    public RunTimeline Apply(RunSessionOpened opened, RunTimeline timeline) => timeline with { Region = opened.Region };

    /// <summary>Closes the previous step's duration and appends the newly-started step.</summary>
    public RunTimeline Apply(StepStarted started, RunTimeline timeline)
    {
        var closed = CloseLastStep(timeline, started.At);
        return closed with { Steps = [.. closed.Steps, new RunTimelineStep(started.Index, started.Kind, started.At, null)] };
    }

    /// <summary>Records one extracted value ref.</summary>
    public RunTimeline Apply(Extracted extracted, RunTimeline timeline) =>
        timeline with { Extracted = [.. timeline.Extracted, new RunTimelineExtract(extracted.Key, extracted.ValueRef)] };

    /// <summary>Records one download's blob ref + metadata.</summary>
    public RunTimeline Apply(Downloaded downloaded, RunTimeline timeline) =>
        timeline with { Downloads = [.. timeline.Downloads, new RunTimelineDownload(downloaded.BlobRef, downloaded.ContentType, downloaded.Size, downloaded.Sha256)] };

    /// <summary>Records one explicit screenshot's ref + metadata, curated like a download — an author-requested
    /// artifact, unlike the finer per-node events the timeline drops.</summary>
    public RunTimeline Apply(Screenshotted shot, RunTimeline timeline) =>
        timeline with { Screenshots = [.. timeline.Screenshots, new RunTimelineScreenshot(shot.ScreenshotRef, shot.Name, shot.Size)] };

    /// <summary>Records one capture's blob ref + metadata (from a <c>capture</c> node or capture-on-failure), curated
    /// like a download — the ref manifest of documents banked to tenant storage.</summary>
    public RunTimeline Apply(Captured captured, RunTimeline timeline) =>
        timeline with { Captures = [.. timeline.Captures, new RunTimelineCapture(captured.BlobRef, captured.Size, captured.Sha256)] };

    /// <summary>Records one missed extraction selector (the soft/strict drift signal), deduped and in first-seen order.
    /// The trace already emits one <c>SelectorMiss</c> per distinct selector per run, so this fold is one entry per
    /// selector even before the guard — the dedupe also keeps a projection rebuild idempotent.</summary>
    public RunTimeline Apply(SelectorMiss miss, RunTimeline timeline) =>
        timeline.MissedSelectors.Contains(miss.Selector, StringComparer.Ordinal)
            ? timeline
            : timeline with { MissedSelectors = [.. timeline.MissedSelectors, miss.Selector] };

    /// <summary>Captures the failing step's screenshot + captureOnFailure HTML refs (carried into the failure at the terminal event).</summary>
    public RunTimeline Apply(StepFailed failed, RunTimeline timeline) => timeline with { ScreenshotRef = failed.ScreenshotRef, CaptureRef = failed.CaptureRef };

    /// <summary>Closes the timeline as succeeded.</summary>
    public RunTimeline Apply(RunSucceeded succeeded, RunTimeline timeline) => Finish(timeline, RunStatus.Succeeded, succeeded.FinishedAt, null);

    /// <summary>Closes the timeline as failed, attaching the failure + screenshot ref.</summary>
    public RunTimeline Apply(RunFailed failed, RunTimeline timeline) => Finish(timeline, RunStatus.Failed, failed.FinishedAt, failed.Failure);

    /// <summary>Closes the timeline as cancelled.</summary>
    public RunTimeline Apply(RunCancelled cancelled, RunTimeline timeline) => Finish(timeline, RunStatus.Cancelled, cancelled.FinishedAt, null);

    // Closes the currently-open (last) step's duration from its start to `at`. A stepless run (a setup failure that never
    // reached a StepStarted) has nothing to close.
    private static RunTimeline CloseLastStep(RunTimeline timeline, DateTimeOffset at)
    {
        if (timeline.Steps.Count == 0)
        {
            return timeline;
        }

        var steps = timeline.Steps.ToList();
        var last = steps[^1];
        steps[^1] = last with { DurationMs = (long)(at - last.At).TotalMilliseconds };
        return timeline with { Steps = steps };
    }

    private static RunTimeline Finish(RunTimeline timeline, RunStatus status, DateTimeOffset finishedAt, RunFailureDetail? failure)
    {
        var closed = CloseLastStep(timeline, finishedAt);
        return closed with
        {
            Status = status,
            FinishedAt = finishedAt,
            DurationMs = (long)(finishedAt - closed.StartedAt).TotalMilliseconds,
            Failure = failure is null
                ? closed.Failure
                : new RunTimelineFailure(failure.Code, failure.Message, failure.AtStep, closed.ScreenshotRef, closed.CaptureRef),
        };
    }
}
