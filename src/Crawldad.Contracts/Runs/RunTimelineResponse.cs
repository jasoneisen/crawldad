namespace Crawldad.Contracts.Runs;

/// <summary>One top-level step in a run's timeline (§13): its index + head kind, when it started (through the
/// <see cref="System.TimeProvider"/> seam), and how long it ran — the elapsed time to the next step's start, or to the
/// run's finish for the last step. Null <see cref="DurationMs"/> means the step never closed (the run is still on it).</summary>
/// <param name="Index">The top-level step index.</param>
/// <param name="Kind">The step's head kind (e.g. <c>goto</c>/<c>loop</c>).</param>
/// <param name="At">When the step started.</param>
/// <param name="DurationMs">How long the step ran (from its start to the next step / run finish), or null if still open.</param>
public sealed record RunTimelineStep(int Index, string Kind, DateTimeOffset At, long? DurationMs);

/// <summary>One value the run bound into its data model (a <c>set</c>/<c>push</c>, §13 <c>Extracted</c>). PII-safe (§12):
/// the <see cref="ValueRef"/> is a shape descriptor (kind + size), <b>never</b> the raw extracted value.</summary>
/// <param name="Key">The target var / list name the value was bound into.</param>
/// <param name="ValueRef">A shape descriptor of the bound value (e.g. <c>string(42)</c>/<c>list(30)</c>/<c>map(6)</c>) — never the value.</param>
public sealed record RunTimelineExtract(string Key, string ValueRef);

/// <summary>One completed download in a run's timeline (§13 <c>Downloaded</c>). Metadata only (§12): the content-addressed
/// blob ref, the guessed content type, the byte size, and the SHA-256 — never the bytes (those stream to blob storage).</summary>
/// <param name="BlobRef">The engine's stored-blob name (content-addressed, <c>{contentId}.{ext}</c>).</param>
/// <param name="ContentType">The content type guessed from the stored name's extension.</param>
/// <param name="Size">The downloaded byte count.</param>
/// <param name="Sha256">The full SHA-256 of the bytes (lowercase hex).</param>
public sealed record RunTimelineDownload(string BlobRef, string ContentType, long Size, string Sha256);

/// <summary>A run's terminal failure as surfaced in its timeline (§13): the typed failure plus the
/// <see cref="ScreenshotRef"/> captured on the failing step (§13 screenshot-on-failure), or null when no screenshot was
/// taken (disabled, no page bound, or a forcibly-cancelled deadline). The screenshot bytes live in deletable blob
/// storage (§12); this is only the ref.</summary>
/// <param name="Code">The stable failure slug.</param>
/// <param name="Message">The (scrubbed) failure message.</param>
/// <param name="AtStep">Where the failure occurred.</param>
/// <param name="ScreenshotRef">The failure screenshot's blob ref, or null when none was captured.</param>
public sealed record RunTimelineFailure(string Code, string Message, RunStepRef AtStep, string? ScreenshotRef);

/// <summary>
/// The <c>GET /runs/{id}/timeline</c> response (§13): the async <c>RunTimeline</c> projection rendered as a Contracts DTO
/// — the ordered step list with durations, the <b>redacted</b> input key names, the extracted-value + blob refs, the
/// terminal failure + screenshot ref, the exact pinned payload revision + script hash, and the backend
/// <see cref="Region"/>. Everything here derives purely from the run's already-<b>scrubbed</b> trace events (§12), so it
/// carries no raw credentials or bulk PII by construction. This is the lag-tolerant cross-run dashboard view (§11), distinct
/// from the read-your-writes <c>GET /runs/{id}</c> state; region is surfaced here (not on <c>RunResponse</c>) to avoid
/// churning the §10 goldens.
/// </summary>
/// <param name="RunId">The run's stream id.</param>
/// <param name="PayloadName">The payload name pinned at start.</param>
/// <param name="ScriptHash">The script hash pinned at start (SHA-256, lowercase hex) — the exact script that ran (drift/audit).</param>
/// <param name="PayloadId">The pinned managed payload (§14.2), or null for an inline run.</param>
/// <param name="PayloadRevision">The pinned payload revision (§14.2), or null for an inline run.</param>
/// <param name="InputKeys">The supplied input key <em>names</em> (redacted — never values, §12).</param>
/// <param name="Region">The backend region the run's session ran in (§9.1), or null before a session opened (a setup-failed / inline run).</param>
/// <param name="Status">The run's disposition.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="FinishedAt">When the run reached a terminal status, or null while still running.</param>
/// <param name="DurationMs">The run's total wall-clock duration, or null while still running.</param>
/// <param name="Steps">The ordered top-level step list with per-step durations.</param>
/// <param name="Extracted">The values bound into the run's data model (key + shape ref, never the value).</param>
/// <param name="Downloads">The downloads streamed to blob storage (refs + metadata, never bytes).</param>
/// <param name="Failure">The terminal failure + screenshot ref, or null when the run did not fail.</param>
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
    RunTimelineFailure? Failure);
