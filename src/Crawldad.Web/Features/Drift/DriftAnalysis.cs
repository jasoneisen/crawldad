using Crawldad.Contracts.Drift;

namespace Crawldad.Web.Features.Drift;

/// <summary>The per-payload drift assessment (issue #47): a pure baseline/delta fold over a payload canary's completed
/// observations. The alarm is deliberately <b>not</b> "any selector missed" — a payload with a legitimate multi-selector
/// fallback (<c>coalesce(text(a), text(b))</c>) has a nonzero steady-state miss floor. Instead the <b>baseline</b> is the
/// union of the misses seen across the earliest <see cref="DefaultBaselineRuns"/> healthy (succeeded) runs, and
/// <b>drift</b> is a selector missing in the latest completed run that was <em>not</em> in that baseline floor — i.e. it
/// matched when the baseline was established and is newly missing now.
///
/// <para>Consequences worth stating: the baseline must be established against healthy runs (a canary that first observes
/// an already-broken site bakes the breakage into its floor); a selector that has missed since the first observation is
/// the floor, never drift; and while the baseline window is still filling the state is <see cref="DriftState.WarmingUp"/>
/// and nothing is alarmed. The signal is observational only — it never changes run behaviour.</para></summary>
public static class DriftAnalysis
{
    /// <summary>How many of a payload's earliest healthy (succeeded) runs establish the baseline miss floor. A small
    /// window smooths single-run noise (a content-dependent fallback that fires on one record) into the floor before
    /// drift is judged; the latest run is compared against it. Also the cap on the baseline query, so the fold stays
    /// bounded however long the canary has run.</summary>
    public const int DefaultBaselineRuns = 3;

    /// <summary>Assesses a payload's drift from its baseline + latest observations. <paramref name="baseline"/> is the
    /// earliest <paramref name="baselineRuns"/> succeeded observations (ascending); <paramref name="current"/> is the
    /// latest completed (succeeded or failed) observation, or null when none exists; <paramref name="observedRuns"/> is
    /// the total completed observation count; <paramref name="threshold"/> is the per-payload count of new misses
    /// tolerated before the status is <see cref="DriftState.Drifted"/> (0 = any new miss drifts).</summary>
    public static PayloadDriftStatus Analyze(
        Guid payloadId,
        string payloadName,
        IReadOnlyList<DriftObservation> baseline,
        DriftObservation? current,
        int observedRuns,
        int baselineRuns,
        int threshold)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var firstObservedAt = baseline.Count > 0 ? baseline[0].ObservedAt : current?.ObservedAt;
        var evidence = current is { } observed
            ? new DriftEvidence(observed.RunId, observed.Status, observed.ObservedAt, observed.FailureScreenshotRef, observed.CaptureRefs, observed.ScreenshotRefs)
            : null;

        DriftState state;
        IReadOnlyList<SelectorDriftDetail> selectors = [];
        var driftedCount = 0;

        if (current is not { } latest)
        {
            // No completed observation at all — nothing to assess.
            state = DriftState.NoData;
        }
        else if (baseline.Count == 0 || baseline.Any(run => run.RunId == latest.RunId))
        {
            // No healthy run to baseline against yet, or the latest completed run is itself still inside the baseline
            // window (no post-baseline run to compare) — warming up, so nothing is alarmed.
            state = DriftState.WarmingUp;
        }
        else
        {
            var floor = baseline.SelectMany(run => run.MissedSelectors).ToHashSet(StringComparer.Ordinal);
            selectors = [.. latest.MissedSelectors
                .Distinct(StringComparer.Ordinal)
                .OrderBy(selector => selector, StringComparer.Ordinal)
                .Select(selector =>
                {
                    var isFloor = floor.Contains(selector);
                    return new SelectorDriftDetail(selector, Drifted: !isFloor, BaselineFloor: isFloor, MissingInLatest: true);
                })];
            driftedCount = selectors.Count(selector => selector.Drifted);
            state = driftedCount > threshold ? DriftState.Drifted : DriftState.Steady;
        }

        return new PayloadDriftStatus(
            payloadId,
            payloadName,
            current?.PayloadRevision,
            state,
            Drifted: state == DriftState.Drifted,
            observedRuns,
            baselineRuns,
            driftedCount,
            threshold,
            firstObservedAt,
            current?.ObservedAt,
            selectors,
            evidence);
    }
}
