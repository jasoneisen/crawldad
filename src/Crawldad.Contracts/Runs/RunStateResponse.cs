using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Runs;

/// <summary>
/// The run-state view (§11) shared by the async control surface: the immediate <c>202</c> body of an <c>async</c>
/// <c>POST /runs</c>, the <c>GET /runs/{id}</c> poll, and the <c>POST /runs/{id}/cancel</c> acknowledgement. It is a
/// superset of the synchronous <see cref="RunResponse"/> that also carries a <see cref="Partial"/> (the result-so-far a
/// cancelled run reports) and makes the terminal-only fields nullable so a still-<see cref="RunStatus.Running"/> run
/// serialises to just <c>{ runId, status }</c>. Exactly the field for the current <see cref="Status"/> is present:
/// <see cref="Result"/> on <see cref="RunStatus.Succeeded"/>, <see cref="Failure"/> on <see cref="RunStatus.Failed"/>,
/// <see cref="Partial"/> on <see cref="RunStatus.Cancelled"/>; <see cref="Stats"/> accompanies any terminal status.
/// </summary>
/// <param name="RunId">The run's stream id.</param>
/// <param name="Status">The run's current disposition (running until the executor saga finishes it, §11).</param>
/// <param name="Result">The payload's evaluated <c>result</c> (present once succeeded; object-literal key order preserved).</param>
/// <param name="Failure">The typed failure (present once failed).</param>
/// <param name="Partial">The result-so-far a cancelled run salvaged (present once cancelled; may be null if none was safe).</param>
/// <param name="Stats">The run counters (present once the run reaches a terminal status).</param>
public sealed record RunStateResponse(
    Guid RunId,
    RunStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunFailureDetail? Failure,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Partial,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunStats? Stats);
