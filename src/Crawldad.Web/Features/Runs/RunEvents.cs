using Crawldad.Contracts.Runs;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The coarse run-lifecycle trace events for a run (§13/§14.2). Deliberately <b>PII-safe</b> (§12): the run stream stores
/// the payload name, the script hash, the input <em>key names</em> only, stats, and (on failure) the typed failure —
/// never the result body or raw input values. When the run pinned a managed payload (§14.2), it also stores the exact
/// <see cref="PayloadId"/> + <see cref="PayloadRevision"/> so editing the payload never mutates historical runs and drift
/// (pinned-vs-head) is detectable; both are null for an inline run. The richer step-level trace events (<c>Navigated</c>,
/// <c>Extracted</c>, …) live in <see cref="RunSessionOpened"/> and its neighbours (RunTraceEvents.cs).
/// </summary>
/// <param name="PayloadName">The payload's logical name (from its <c>name</c> field).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of the executed payload JSON — pins exactly what ran (drift/audit).</param>
/// <param name="StartedAt">When the run started (through the <see cref="TimeProvider"/> seam).</param>
/// <param name="InputKeys">The names of the supplied inputs — never their values (PII discipline).</param>
/// <param name="PayloadId">The pinned managed payload (§14.2), or null for an inline run.</param>
/// <param name="PayloadRevision">The pinned payload revision (§14.2), or null for an inline run.</param>
public sealed record RunStarted(
    string PayloadName,
    string ScriptHash,
    DateTimeOffset StartedAt,
    IReadOnlyList<string> InputKeys,
    Guid? PayloadId,
    int? PayloadRevision);

/// <summary>The run completed successfully.</summary>
/// <param name="Stats">The run counters.</param>
/// <param name="FinishedAt">When the run finished.</param>
public sealed record RunSucceeded(RunStats Stats, DateTimeOffset FinishedAt);

/// <summary>The run ended in a typed failure (§8.3).</summary>
/// <param name="Failure">The failure class/code/message and the step it occurred at.</param>
/// <param name="Stats">The run counters at failure.</param>
/// <param name="FinishedAt">When the run finished.</param>
public sealed record RunFailed(RunFailureDetail Failure, RunStats Stats, DateTimeOffset FinishedAt);

/// <summary>
/// A <c>log</c> node fired (§13 <c>LogEmitted</c>): part of the run's trace, appended in step order even when the run
/// later fails. Warnings are <b>not</b> failures (§8.3) — the run continues. PII discipline (§12): a payload can
/// interpolate extracted text into a <c>${…}</c> message, so this is metadata authored by the payload, not raw input.
/// </summary>
/// <param name="Level">The severity the <c>log</c> node declared (<c>info</c>/<c>warning</c>/<c>error</c>).</param>
/// <param name="Message">The rendered message (its <c>${…}</c> interpolations already resolved).</param>
/// <param name="At">When the log fired (through the <see cref="TimeProvider"/> seam).</param>
public sealed record LogEmitted(string Level, string Message, DateTimeOffset At);

/// <summary>
/// One retryable attempt failed and is being retried (§8.3): a coarse trace marker so retries are observable. Appended
/// only when an attempt fails on a retryable condition <em>and</em> attempts remain; the final attempt's failure is
/// carried by <see cref="RunFailed"/> instead. On a <c>pageCrashed</c> attempt the interpreter also reopens the page
/// (§3.6) before the next attempt.
/// </summary>
/// <param name="Attempt">The 1-based number of the attempt that failed.</param>
/// <param name="Code">The retryable failure code (<c>timeout</c>/<c>pageCrashed</c>, or a retryable <c>fail</c>'s code).</param>
/// <param name="At">When the attempt failed (through the <see cref="TimeProvider"/> seam).</param>
public sealed record RunAttemptFailed(int Attempt, string Code, DateTimeOffset At);

/// <summary>
/// The run passed a declared <c>checkpoint</c> (§11): a <b>metadata-only</b> trace marker (name + sequence, never the
/// cursor or the var snapshot — those are bulk state and live in the executor's durable saga/progress storage, §12). The
/// executor appends this from its own Marten session <em>as the checkpoint is reached</em>, so it is durable mid-run and a
/// killed run's progress is observable up to its last checkpoint.
/// </summary>
/// <param name="Name">The checkpoint's declared name.</param>
/// <param name="Sequence">The monotonic per-run checkpoint number.</param>
/// <param name="At">When the checkpoint was reached (through the <see cref="TimeProvider"/> seam).</param>
public sealed record RunCheckpointReached(string Name, int Sequence, DateTimeOffset At);

/// <summary>
/// A killed run was resumed from its last checkpoint (§11): appended by the executor when a redelivered run re-establishes
/// a fresh browser session and re-enters at the checkpoint's top-level step. Its presence in a run's trace is the
/// observable proof that resume — not a restart from step 0 — occurred.
/// </summary>
/// <param name="FromStepIndex">The top-level step index the run resumed at.</param>
/// <param name="CheckpointName">The checkpoint the run resumed from.</param>
/// <param name="At">When the run resumed (through the <see cref="TimeProvider"/> seam).</param>
public sealed record RunResumed(int FromStepIndex, string CheckpointName, DateTimeOffset At);

/// <summary>
/// A cooperative cancel was requested for the run (§11): appended by <c>POST /runs/{id}/cancel</c>. The interpreter
/// honours it between steps and the run then reaches <see cref="RunCancelled"/>. Metadata only — carries no caller data.
/// </summary>
/// <param name="At">When the cancel was requested (through the <see cref="TimeProvider"/> seam).</param>
public sealed record RunCancellationRequested(DateTimeOffset At);

/// <summary>
/// The run was cancelled (§11): the interpreter stopped between steps and the backend session was torn down cleanly (no
/// orphaned session). Like the other terminal events it carries stats only — the salvaged <c>partial</c> result body is
/// bulk data held in the deletable run-progress store, never in this immutable trace (§12).
/// </summary>
/// <param name="Stats">The run counters at cancellation.</param>
/// <param name="FinishedAt">When the run finished tearing down.</param>
public sealed record RunCancelled(RunStats Stats, DateTimeOffset FinishedAt);
