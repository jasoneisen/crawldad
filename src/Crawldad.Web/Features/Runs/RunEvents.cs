using Crawldad.Contracts.Runs;

namespace Crawldad.Web.Features.Runs;

/// <summary>The coarse run-lifecycle opening event. Deliberately PII-safe: stores the payload name, script hash, input
/// <em>key names</em> only — never the result body or raw input values. <see cref="PayloadId"/>/<see cref="PayloadRevision"/>
/// pin the exact managed-payload revision executed (null for an inline run), so drift is detectable later.</summary>
public sealed record RunStarted(
    string PayloadName,
    string ScriptHash,
    DateTimeOffset StartedAt,
    IReadOnlyList<string> InputKeys,
    Guid? PayloadId,
    int? PayloadRevision);

/// <summary>The opening event of a run <b>queued</b> at the concurrent-run cap: the durable, restart-surviving record
/// that the run exists and is waiting. Carries the same PII-safe fields as <see cref="RunStarted"/> plus the enqueue
/// instant; reaches <see cref="RunDequeued"/> when a slot frees (a run started immediately uses <see cref="RunStarted"/> instead).</summary>
public sealed record RunQueued(
    string PayloadName,
    string ScriptHash,
    DateTimeOffset QueuedAt,
    IReadOnlyList<string> InputKeys,
    Guid? PayloadId,
    int? PayloadRevision);

/// <summary>The queued→running transition, appended at promotion right before the durable executor is kicked. Carries
/// the run's realised queue wait (the p95 queue-wait datum). The run's wall-clock deadline is scheduled from here, not
/// from admission, so time spent queued does not count against it.</summary>
public sealed record RunDequeued(DateTimeOffset StartedAt, long QueueWaitMs);

/// <summary>The run completed successfully.</summary>
public sealed record RunSucceeded(RunStats Stats, DateTimeOffset FinishedAt);

/// <summary>The run ended in a typed failure.</summary>
public sealed record RunFailed(RunFailureDetail Failure, RunStats Stats, DateTimeOffset FinishedAt);

/// <summary>A <c>log</c> node fired: appended in step order even when the run later fails. Warnings are <b>not</b>
/// failures — the run continues. A payload can interpolate extracted text into its message, so this is
/// payload-authored metadata, not raw input.</summary>
public sealed record LogEmitted(string Level, string Message, DateTimeOffset At);

/// <summary>One retryable attempt failed and is being retried: appended only when the failure is retryable and attempts
/// remain (the final attempt's failure is <see cref="RunFailed"/> instead). A <c>pageCrashed</c> attempt also has the
/// interpreter reopen the page before the next attempt.</summary>
public sealed record RunAttemptFailed(int Attempt, string Code, DateTimeOffset At);

/// <summary>A backend <b>connect</b> attempt failed transiently and is being retried under <c>config.connectRetry</c>:
/// appended before the backoff wait, only when the connect fault was transient and attempts remain (the final attempt's
/// exhaustion is the terminal <see cref="RunFailed"/> <c>backend_unavailable</c> instead). Distinct from
/// <see cref="RunAttemptFailed"/>, which retries the post-connect program on an already-established session — this
/// re-establishes the connection, re-reading the credentialRef so a connector's mid-window re-registration is picked up.
/// PII-safe: <see cref="Code"/> is the fixed <c>backend_unavailable</c> slug, never the connect URL or key.</summary>
public sealed record RunConnectAttemptFailed(int Attempt, string Code, DateTimeOffset At);

/// <summary>The run passed a declared <c>checkpoint</c>: a metadata-only marker (name + sequence, never the cursor or
/// var snapshot — those are bulk state in the executor's durable progress storage). Appended as the checkpoint is
/// reached, so a killed run's progress is observable up to its last checkpoint.</summary>
public sealed record RunCheckpointReached(string Name, int Sequence, DateTimeOffset At);

/// <summary>A killed run was resumed from its last checkpoint: appended when a redelivered run re-establishes a fresh
/// browser session and re-enters at the checkpoint's top-level step. Its presence is the observable proof that resume
/// — not a restart from step 0 — occurred.</summary>
public sealed record RunResumed(int FromStepIndex, string CheckpointName, DateTimeOffset At);

/// <summary>A cooperative cancel was requested: appended by <c>POST /runs/{id}/cancel</c>. The interpreter honours it
/// between steps and the run then reaches <see cref="RunCancelled"/>. Metadata only — carries no caller data.</summary>
public sealed record RunCancellationRequested(DateTimeOffset At);

/// <summary>The run was cancelled: the interpreter stopped between steps and the backend session was torn down cleanly.
/// Carries stats only — the salvaged <c>partial</c> result is bulk data held in the deletable run-progress store, never
/// in this immutable trace.</summary>
public sealed record RunCancelled(RunStats Stats, DateTimeOffset FinishedAt);
