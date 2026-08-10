using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Runs;

/// <summary>The run-state view for the async control surface (<c>202</c> body, <c>GET /runs/{id}</c> poll,
/// <c>cancel</c> ack): terminal fields are nullable so a running run serialises to just <c>{ runId, status }</c>;
/// exactly the field matching the current <see cref="Status"/> is populated.</summary>
public sealed record RunStateResponse(
    Guid RunId,
    RunStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunFailureDetail? Failure,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Partial,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunStats? Stats,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Position = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? QueueWaitMs = null);
