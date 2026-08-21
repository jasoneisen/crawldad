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

/// <summary>One document a <c>capture</c> node (or capture-on-failure) streamed to tenant BYO storage. Metadata only:
/// content-addressed blob ref, byte size, and SHA-256 — never the HTML (which lives in the customer's own storage,
/// under the customer's own retention).</summary>
public sealed record RunTimelineCapture(string BlobRef, long Size, string Sha256);

/// <summary>A run's terminal failure as surfaced in its timeline: the typed failure plus the failing step's artifact refs.
/// <see cref="ScreenshotRef"/> is the failure screenshot's ref (null when none was taken — disabled, no page bound, or a
/// cancelled deadline). <see cref="CaptureRef"/> is the <c>config.captureOnFailure</c> HTML document's ref: the same
/// content-addressed ref as its entry in the response's <c>captures[]</c>, so a consumer correlates the failing page to
/// its captured document explicitly by ref rather than by position (null when capture-on-failure was disabled or captured nothing).</summary>
public sealed record RunTimelineFailure(string Code, string Message, RunStepRef AtStep, string? ScreenshotRef, string? CaptureRef);

/// <summary>The <c>GET /runs/{id}/timeline</c> response: the <c>RunTimeline</c> projection as a DTO — ordered steps
/// with durations, redacted input key names, extracted/blob refs, the missed extraction selectors (the per-run drift
/// signal, issue #47), and the failure + its screenshot and capture refs.</summary>
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
    IReadOnlyList<RunTimelineCapture> Captures,
    IReadOnlyList<string> MissedSelectors,
    RunTimelineFailure? Failure);
