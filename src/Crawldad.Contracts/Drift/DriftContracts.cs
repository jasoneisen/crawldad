using System.Text.Json.Serialization;
using Crawldad.Contracts.Runs;

namespace Crawldad.Contracts.Drift;

/// <summary>A payload canary's drift disposition (issue #47). Serialized camelCase via <see cref="ContractsJson"/>.
/// The signal is <b>baseline/delta</b>, not a naive "any selector missed": a selector that has missed since the
/// baseline was first established is the steady-state floor (a legitimate multi-selector fallback), not drift — only a
/// selector that matched at baseline and is <em>newly</em> missing counts.</summary>
public enum DriftState
{
    /// <summary>No completed canary run has been observed for this payload yet — there is nothing to assess.</summary>
    NoData,

    /// <summary>Observations exist but the baseline is not yet established (fewer than the baseline-window's worth of
    /// healthy runs, or no run after the baseline window to compare) — drift is deliberately not alarmed while warming up.</summary>
    WarmingUp,

    /// <summary>The baseline is established and the latest run introduced no new selector misses beyond it (allowing
    /// for the configured threshold) — the canary is healthy.</summary>
    Steady,

    /// <summary>The latest run is missing selectors that matched at baseline, above the configured threshold — the
    /// payload's extraction has drifted from the site it targets.</summary>
    Drifted,
}

/// <summary>One selector's status in the latest observed canary run. <see cref="Selector"/> is the declared selector
/// text (scrubbed defensively, never page content). A selector is <see cref="Drifted"/> when it is
/// <see cref="MissingInLatest"/> but was <b>not</b> part of the baseline miss floor; a <see cref="BaselineFloor"/>
/// selector missed since the baseline was established (a legitimate steady-state miss, e.g. a <c>coalesce</c> fallback),
/// so its continued miss is expected, not drift.</summary>
public sealed record SelectorDriftDetail(
    string Selector,
    bool Drifted,
    bool BaselineFloor,
    bool MissingInLatest);

/// <summary>The evidence attached to a drift assessment: the latest observed run whose miss set drove the signal, so a
/// drift alert arrives with the changed page in hand. Carries refs only — never bytes: the <c>capture</c> blobs (a
/// <c>capture</c> node or capture-on-failure, streamed to the tenant's own storage), explicit <c>screenshot</c> refs,
/// and the failing-step screenshot ref when the run failed. Retrieve a screenshot via
/// <c>GET /runs/{runId}/screenshots/{reference}</c>; capture blobs live in the tenant's BYO storage.</summary>
public sealed record DriftEvidence(
    Guid RunId,
    RunStatus Status,
    DateTimeOffset ObservedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureScreenshotRef,
    IReadOnlyList<string> CaptureRefs,
    IReadOnlyList<string> ScreenshotRefs);

/// <summary>The <c>GET /payloads/{id}/drift-status</c> response: a payload canary's per-selector drift assessment
/// (issue #47), computed on read from the runs' <c>RunTimeline</c> observations under a baseline/delta model. A drift
/// monitor polls this per pinned payload; <see cref="Drifted"/> is the boolean alarm and <see cref="DriftedSelectorCount"/>
/// the metric a per-payload <c>threshold</c> is applied to.</summary>
public sealed record PayloadDriftStatus(
    Guid PayloadId,
    string PayloadName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PinnedRevision,
    DriftState State,
    bool Drifted,
    int ObservedRuns,
    int BaselineRuns,
    int DriftedSelectorCount,
    int Threshold,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? FirstObservedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastObservedAt,
    IReadOnlyList<SelectorDriftDetail> Selectors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DriftEvidence? Evidence);
