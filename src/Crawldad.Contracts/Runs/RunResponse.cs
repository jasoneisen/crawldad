using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Runs;

/// <summary>Run counters (<c>stats</c>): <c>steps</c> counts loop bodies per iteration; <c>cacheHits</c> is always 0
/// until the route cache lands.</summary>
public sealed record RunStats(long DurationMs, int Steps, int Requests, int CacheHits, int Downloads);

/// <summary>Where a failure occurred (<c>failure.atStep</c>).</summary>
/// <param name="Index">The top-level step index being executed.</param>
/// <param name="Kind">The failing node's head key (e.g. <c>loop</c>), or <c>config</c> before the steps run.</param>
public sealed record RunStepRef(int Index, string Kind);

/// <summary>A typed run failure. <see cref="Class"/> is <c>"terminal"</c> or <c>"retryable-exhausted"</c> (a single
/// attempt was made); <see cref="Code"/> is a stable slug.</summary>
public sealed record RunFailureDetail(string Class, string Code, string Message, RunStepRef AtStep);

/// <summary>The <c>POST /runs</c> response: one request, one structured response — a failed run is still HTTP 200.
/// Exactly one of <see cref="Result"/>/<see cref="Failure"/> is present; <see cref="Result"/> preserves object-literal
/// key order.</summary>
public sealed record RunResponse(
    Guid RunId,
    RunStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunFailureDetail? Failure,
    RunStats Stats);
