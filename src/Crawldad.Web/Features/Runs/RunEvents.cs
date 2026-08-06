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
