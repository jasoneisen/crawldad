using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Runs;

/// <summary>Run counters (§10 <c>stats</c>). <c>cacheHits</c>/<c>downloads</c> are always 0 in Phase 1 (no route cache,
/// no downloads yet).</summary>
/// <param name="DurationMs">Wall-clock duration measured through the <see cref="TimeProvider"/> seam.</param>
/// <param name="Steps">Executed node count (each dispatched node, loop bodies re-counted per iteration).</param>
/// <param name="Requests">Navigations plus matched <c>waitForRequest</c>s.</param>
/// <param name="CacheHits">Route-cache hits (0 in Phase 1).</param>
/// <param name="Downloads">Completed downloads (0 in Phase 1).</param>
public sealed record RunStats(long DurationMs, int Steps, int Requests, int CacheHits, int Downloads);

/// <summary>Where a failure occurred (§10 <c>failure.atStep</c>).</summary>
/// <param name="Index">The top-level step index being executed.</param>
/// <param name="Kind">The head key of the failing node (e.g. <c>loop</c>), or <c>config</c> before the steps run.</param>
public sealed record RunStepRef(int Index, string Kind);

/// <summary>A typed run failure (§8.3/§10). <see cref="Class"/> is <c>"terminal"</c> or <c>"retryable-exhausted"</c>
/// (the latter because Phase 1 makes a single attempt); <see cref="Code"/> is a stable slug.</summary>
/// <param name="Class">The failure class the caller branches on.</param>
/// <param name="Code">The stable failure slug (e.g. <c>unknown_backend_adapter</c>, <c>index_out_of_range</c>).</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="AtStep">Where the failure occurred.</param>
public sealed record RunFailureDetail(string Class, string Code, string Message, RunStepRef AtStep);

/// <summary>
/// The <c>POST /runs</c> response (§10). One request → one structured response; a failed <em>run</em> is still HTTP
/// 200 (the request succeeded). Exactly one of <see cref="Result"/>/<see cref="Failure"/> is present.
/// </summary>
/// <param name="RunId">The run's stream id.</param>
/// <param name="Status">Succeeded or failed.</param>
/// <param name="Result">The payload's evaluated <c>result</c> (present on success; object-literal key order preserved).</param>
/// <param name="Failure">The typed failure (present on failure).</param>
/// <param name="Stats">The run counters.</param>
public sealed record RunResponse(
    Guid RunId,
    RunStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunFailureDetail? Failure,
    RunStats Stats);
