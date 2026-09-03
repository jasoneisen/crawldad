using Crawldad.Contracts.Runs;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Crawldad.Api.Features.Runs;

/// <summary>The headline counters folded onto a <see cref="RunSummary"/> at terminal — the list-view subset of the run's
/// full <see cref="RunStats"/> (steps/requests/selector-misses). Stored on the summary so the runs list needs neither the
/// per-run <see cref="RunProgress"/> nor the heavier <see cref="RunTimeline"/> to render a row.</summary>
public sealed record RunSummaryStats(int Steps, int Requests, int SelectorMisses);

/// <summary>The failure headline folded onto a <see cref="RunSummary"/> when a run fails — the class and code only (the
/// two fields a list row shows), atomically null-or-both so the list mapping needs no defensive fallback.</summary>
public sealed record RunSummaryFailure(string Class, string Code);

/// <summary>The lightweight cross-run listing read model behind <c>GET /runs</c>: one small row per run folded from its
/// event stream, carrying exactly the list-view fields (status, start, duration, region, pinned-payload identity, headline
/// stats, failure class/code, and the run's total event count) and nothing heavy. Distinct from the read-your-writes
/// <see cref="RunProgress"/> (which lacks start/region/payload identity) and the full <see cref="RunTimeline"/> (whose
/// per-step arrays are far more than a list needs) — a purpose-built, index-friendly summary. Exposed as
/// <see cref="RunListItem"/>, never this document directly.</summary>
public sealed record RunSummary
{
    /// <summary>The run id (the event-stream id).</summary>
    public Guid Id { get; init; }

    /// <summary>The run's disposition: <c>queued</c>/<c>running</c> until a terminal event lands.</summary>
    public RunStatus Status { get; init; } = RunStatus.Running;

    /// <summary>The payload name pinned at start (an inline run still names itself; "unnamed" when it did not).</summary>
    public string PayloadName { get; init; } = "";

    /// <summary>The pinned managed payload id, or null for an inline run (the <c>inline</c> marker in the list row).</summary>
    public Guid? PayloadId { get; init; }

    /// <summary>The pinned payload revision, or null for an inline run.</summary>
    public int? PayloadRevision { get; init; }

    /// <summary>The backend region the session ran in, or null before a session opened.</summary>
    public string? Region { get; init; }

    /// <summary>When the run started — the enqueue instant while queued, overwritten with the real execution start at
    /// promotion — so the list orders and time-filters on a single, sane field.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the run reached a terminal status, or null while still queued/running.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The run's total duration (from its terminal stats), or null while still queued/running.</summary>
    public long? DurationMs { get; init; }

    /// <summary>The headline counters (set at any terminal status), else null.</summary>
    public RunSummaryStats? Stats { get; init; }

    /// <summary>The failure class + code when the run failed, else null.</summary>
    public RunSummaryFailure? Failure { get; init; }

    /// <summary>The run's total event-stream length at terminal (the terminal event's stream version), or null while
    /// still queued/running. The exact "events per run" the <c>max-events-per-run</c> guardrail bounds, captured for
    /// <c>GET /usage</c> without a metrics projection.</summary>
    public int? EventCount { get; init; }
}

/// <summary>Folds a run's lifecycle events into its <see cref="RunSummary"/> row. Reacts to the coarse lifecycle events
/// only — the opener, the queue promotion, the session-open (region), and the three terminals — and ignores the fine
/// step trace the <see cref="RunTimelineProjection"/> curates, so the summary stays cheap to build and small to query.
/// Registered on the shared, config-driven projection lifecycle (Inline under the test switch, Async in production).</summary>
public sealed class RunSummaryProjection : SingleStreamProjection<RunSummary, Guid>
{
    /// <summary>Opens the row on a run started immediately under the cap.</summary>
    public RunSummary Create(RunStarted started) => new()
    {
        PayloadName = started.PayloadName,
        PayloadId = started.PayloadId,
        PayloadRevision = started.PayloadRevision,
        StartedAt = started.StartedAt,
        Status = RunStatus.Running,
    };

    /// <summary>Opens the row on a run queued at the cap; <see cref="RunSummary.StartedAt"/> is seeded to the enqueue
    /// instant (a run cancelled/expired while still queued keeps that baseline; a promoted run overwrites it below).</summary>
    public RunSummary Create(RunQueued queued) => new()
    {
        PayloadName = queued.PayloadName,
        PayloadId = queued.PayloadId,
        PayloadRevision = queued.PayloadRevision,
        StartedAt = queued.QueuedAt,
        Status = RunStatus.Queued,
    };

    /// <summary>Stamps the real execution start (and the running status) when a queued run is promoted.</summary>
    public RunSummary Apply(RunDequeued dequeued, RunSummary current) =>
        current with { StartedAt = dequeued.StartedAt, Status = RunStatus.Running };

    /// <summary>Records the backend region once the session opened.</summary>
    public RunSummary Apply(RunSessionOpened opened, RunSummary current) => current with { Region = opened.Region };

    /// <summary>Closes the row as succeeded, capturing stats and the run's total event count (the event's stream version).</summary>
    public RunSummary Apply(IEvent<RunSucceeded> succeeded, RunSummary current) =>
        Finish(current, RunStatus.Succeeded, succeeded.Data.FinishedAt, succeeded.Data.Stats, null, succeeded.Version);

    /// <summary>Closes the row as failed, capturing the failure class/code, stats, and total event count.</summary>
    public RunSummary Apply(IEvent<RunFailed> failed, RunSummary current) =>
        Finish(current, RunStatus.Failed, failed.Data.FinishedAt, failed.Data.Stats, new RunSummaryFailure(failed.Data.Failure.Class, failed.Data.Failure.Code), failed.Version);

    /// <summary>Closes the row as cancelled, capturing stats and total event count.</summary>
    public RunSummary Apply(IEvent<RunCancelled> cancelled, RunSummary current) =>
        Finish(current, RunStatus.Cancelled, cancelled.Data.FinishedAt, cancelled.Data.Stats, null, cancelled.Version);

    private static RunSummary Finish(RunSummary current, RunStatus status, DateTimeOffset finishedAt, RunStats stats, RunSummaryFailure? failure, long version) =>
        current with
        {
            Status = status,
            FinishedAt = finishedAt,
            DurationMs = stats.DurationMs,
            Stats = new RunSummaryStats(stats.Steps, stats.Requests, stats.SelectorMisses),
            Failure = failure,
            EventCount = (int)version,
        };
}
