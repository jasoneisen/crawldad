using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Runs;

/// <summary>A run's terminal failure as surfaced in the runs list: the stable failure <see cref="Class"/>
/// (<c>terminal</c> / <c>retryable-exhausted</c>) and <see cref="Code"/> slug — the two headline fields a list row shows.
/// The full message, failing step, and screenshot/capture refs are on the run-detail surfaces (poll + timeline), never
/// duplicated into a list row.</summary>
public sealed record RunListFailure(string Class, string Code);

/// <summary>The headline counters a runs-list row carries — a compact subset of the run's full <see cref="RunStats"/>:
/// the semantic <see cref="Steps"/> run, the <see cref="Requests"/> issued, and the <see cref="SelectorMisses"/> (the
/// soft drift signal). Present only once a run reaches a terminal status; a running/queued row omits it.</summary>
public sealed record RunListStats(int Steps, int Requests, int SelectorMisses);

/// <summary>One row of <c>GET /runs</c>: a run's list-view summary. <see cref="Inline"/> is the explicit marker for a run
/// launched from an inline payload document (no managed <see cref="PayloadId"/>/<see cref="PayloadRevision"/>); a pinned
/// run carries both plus the <see cref="PayloadName"/>. Terminal-only fields (<see cref="DurationMs"/>,
/// <see cref="Failure"/>, <see cref="Stats"/>) and the pre-session <see cref="Region"/> are omitted while absent, so a
/// running/queued row serialises lean.</summary>
public sealed record RunListItem(
    Guid RunId,
    RunStatus Status,
    DateTimeOffset StartedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunListFailure? Failure,
    string PayloadName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PayloadId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PayloadRevision,
    bool Inline,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Region,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunListStats? Stats);

/// <summary>The <c>GET /runs</c> response: a tenant-scoped, filtered, offset-paginated page of run summaries newest-first
/// (by <c>startedAt</c>, run id as the stable tiebreaker). <see cref="Total"/> is the count across the whole filtered set
/// (not just this page); <see cref="HasMore"/> is true when a further page exists. The rows carry list-view fields only —
/// full result/timeline/drift live on the per-run surfaces.</summary>
public sealed record RunListResponse(
    IReadOnlyList<RunListItem> Runs,
    int Page,
    int Size,
    int Total,
    bool HasMore);
