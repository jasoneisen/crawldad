using Crawldad.Contracts.Runs;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The coarse Phase 1 trace events for a run (§13/§14.2). Deliberately <b>PII-safe</b> (§12): the run stream stores
/// the payload name, the script hash, the input <em>key names</em> only, stats, and (on failure) the typed failure —
/// never the result body or raw input values. Richer step-level events (<c>Navigated</c>, <c>Extracted</c>, …) and
/// the executor saga arrive in later phases.
/// </summary>
/// <param name="PayloadName">The payload's logical name (from its <c>name</c> field).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of the inline payload JSON — pins exactly what ran (drift/audit).</param>
/// <param name="StartedAt">When the run started (through the <see cref="TimeProvider"/> seam).</param>
/// <param name="InputKeys">The names of the supplied inputs — never their values (PII discipline).</param>
public sealed record RunStarted(string PayloadName, string ScriptHash, DateTimeOffset StartedAt, IReadOnlyList<string> InputKeys);

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
