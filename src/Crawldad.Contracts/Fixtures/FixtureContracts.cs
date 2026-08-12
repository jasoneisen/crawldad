using System.Text.Json;
using System.Text.Json.Serialization;
using Crawldad.Contracts.Runs;

namespace Crawldad.Contracts.Fixtures;

/// <summary>One tenant fixture set as returned by <c>POST /fixtures/{name}/record</c> and each row of
/// <c>GET /fixtures</c>: the recorded state-machine's shape (page + transition counts, byte size), the record run that
/// produced it, and when. Never carries page HTML — a listing is a compact catalogue of the tenant's replay assets.</summary>
public sealed record FixtureSummary(
    string Name,
    int PageCount,
    int TransitionCount,
    long TotalBytes,
    Guid RunId,
    DateTimeOffset CreatedAt);

/// <summary>The <c>GET /fixtures</c> response: every fixture set the authenticated tenant has recorded, ordered by name.</summary>
public sealed record FixtureListResponse(IReadOnlyList<FixtureSummary> Fixtures);

/// <summary>The <c>GET /fixtures/{name}</c> response: the set summary plus the recorded manifest itself (the initial
/// state, each state's URL, and the transition graph) so a tenant can inspect exactly what coverage a replay has. The
/// manifest references page HTML only by its content hash — the bytes are never surfaced here.</summary>
public sealed record FixtureDetailResponse(FixtureSummary Summary, JsonElement Manifest);

/// <summary>The <c>POST /fixtures/{name}/record</c> body: a payload to execute against its own configured backend while
/// banking each page state into the named fixture set. Shaped like <c>POST /runs</c> — an inline <see cref="Payload"/>
/// document plus the run <see cref="Inputs"/> (the backend binding, credentialRefs, and any parameters).</summary>
public sealed record RecordFixtureRequest(JsonElement Payload, JsonElement Inputs);

/// <summary>The <c>POST /fixtures/{name}/record</c> response: the record run's disposition (a failed record run is still
/// HTTP 200, exactly like <c>POST /runs</c>) with the recorded <see cref="Fixture"/> and the run's own
/// <see cref="Result"/> on success, or the classified <see cref="Failure"/> when the session could not be recorded
/// faithfully (a divergence, or an operation the recorder cannot capture) — in which case no set is persisted.</summary>
public sealed record RecordFixtureResponse(
    Guid RunId,
    RunStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FixtureSummary? Fixture,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunFailureDetail? Failure,
    RunStats Stats);
