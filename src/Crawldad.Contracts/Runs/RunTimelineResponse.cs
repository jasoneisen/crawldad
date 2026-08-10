namespace Crawldad.Contracts.Runs;

/// <summary>One top-level step in a run's timeline: index, head kind, start time, and duration (elapsed to the next
/// step's start, or the run's finish for the last step). Null <see cref="DurationMs"/> means the step never closed.</summary>
public sealed record RunTimelineStep(int Index, string Kind, DateTimeOffset At, long? DurationMs);

/// <summary>One value the run bound into its data model. PII-safe: <see cref="ValueRef"/> is a shape descriptor
/// (kind + size), never the raw extracted value.</summary>
public sealed record RunTimelineExtract(string Key, string ValueRef);

/// <summary>One completed download in a run's timeline. Metadata only: content-addressed blob ref, guessed content
/// type, byte size, and SHA-256 — never the bytes (those stream to blob storage).</summary>
public sealed record RunTimelineDownload(string BlobRef, string ContentType, long Size, string Sha256);

/// <summary>One explicit <c>screenshot</c> node's capture. Metadata only: content-addressed blob ref, optional author
/// label, and PNG byte size — never the image (lives in the deletable, TTL-governed screenshot blob store).</summary>
public sealed record RunTimelineScreenshot(string ScreenshotRef, string? Name, long Size);

/// <summary>A run's terminal failure as surfaced in its timeline: the typed failure plus the screenshot ref captured
/// on the failing step, or null when none was taken (disabled, no page bound, or a cancelled deadline).</summary>
public sealed record RunTimelineFailure(string Code, string Message, RunStepRef AtStep, string? ScreenshotRef);

/// <summary>The <c>GET /runs/{id}/timeline</c> response: the <c>RunTimeline</c> projection as a DTO — ordered steps
/// with durations, redacted input key names, extracted/blob refs, and the failure + screenshot ref.</summary>
public sealed record RunTimelineResponse(
    Guid RunId,
    string PayloadName,
    string ScriptHash,
    Guid? PayloadId,
    int? PayloadRevision,
    IReadOnlyList<string> InputKeys,
    string? Region,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    IReadOnlyList<RunTimelineStep> Steps,
    IReadOnlyList<RunTimelineExtract> Extracted,
    IReadOnlyList<RunTimelineDownload> Downloads,
    IReadOnlyList<RunTimelineScreenshot> Screenshots,
    RunTimelineFailure? Failure);
