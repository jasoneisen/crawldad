using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Runs;

/// <summary>
/// The run-state view (§11) shared by the async control surface: the immediate <c>202</c> body of an <c>async</c>
/// <c>POST /runs</c> (or of a run queued at the concurrent-run cap, CD-16), the <c>GET /runs/{id}</c> poll, and the
/// <c>POST /runs/{id}/cancel</c> acknowledgement. It is a superset of the synchronous <see cref="RunResponse"/> that also
/// carries a <see cref="Partial"/> (the result-so-far a cancelled run reports) and makes the terminal-only fields nullable so a
/// still-<see cref="RunStatus.Running"/> run serialises to just <c>{ runId, status }</c>. Exactly the field for the current
/// <see cref="Status"/> is present: <see cref="Position"/> on <see cref="RunStatus.Queued"/>, <see cref="Result"/> on
/// <see cref="RunStatus.Succeeded"/>, <see cref="Failure"/> on <see cref="RunStatus.Failed"/>, <see cref="Partial"/> on
/// <see cref="RunStatus.Cancelled"/>; <see cref="Stats"/> accompanies any terminal status, and <see cref="QueueWaitMs"/> is
/// present once a run that queued has started (the enqueue→execution-start wait, the CD-16 upgrade signal).
/// </summary>
/// <param name="RunId">The run's stream id.</param>
/// <param name="Status">The run's current disposition (queued behind the cap, running, or terminal, §11/CD-16).</param>
/// <param name="Result">The payload's evaluated <c>result</c> (present once succeeded; object-literal key order preserved).</param>
/// <param name="Failure">The typed failure (present once failed).</param>
/// <param name="Partial">The result-so-far a cancelled run salvaged (present once cancelled; may be null if none was safe).</param>
/// <param name="Stats">The run counters (present once the run reaches a terminal status).</param>
/// <param name="Position">The run's 1-based place in its tenant's FIFO admission queue (present only while
/// <see cref="RunStatus.Queued"/>, CD-16); computed on read, so it decreases as earlier runs promote.</param>
/// <param name="QueueWaitMs">How long the run waited in the queue before it started, in milliseconds (present once a queued run
/// has been promoted to running, CD-16) — the per-run datum the tenant's p95 queue wait aggregates from.</param>
public sealed record RunStateResponse(
    Guid RunId,
    RunStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunFailureDetail? Failure,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Partial,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunStats? Stats,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Position = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? QueueWaitMs = null);
