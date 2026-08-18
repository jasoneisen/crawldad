using System.Globalization;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Features.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Drift;

/// <summary><c>GET /payloads/{id}/drift-status</c>: a payload canary's per-selector drift assessment (issue #47),
/// computed on read from the payload's runs under the baseline/delta model in <see cref="DriftAnalysis"/>. A drift
/// monitor (e.g. a scheduled re-run of a pinned revision against the live site or a fixture-replay baseline) polls this
/// per payload; the response's <c>drifted</c> flag is the alarm and <c>driftedSelectorCount</c> the metric an optional
/// per-payload <c>?threshold=N</c> query tunes.
///
/// <para>Tenant-scoped like every read: the payload aggregate and the run timelines are read from the request's
/// tenant partition, so an unknown or foreign payload is a 404 with no existence oracle. Distinct from
/// <c>GET /runs/{id}/drift</c>, which reports one run's pinned-vs-head payload-<em>revision</em> drift; this reports a
/// payload's <em>selector</em> drift across its canary history.</para></summary>
public static class PayloadDriftStatusEndpoint
{
    /// <summary>Handles <c>GET /payloads/{id}/drift-status</c>. Fixes the canary's current revision from the latest
    /// completed observation, then reads at most <see cref="DriftAnalysis.DefaultBaselineRuns"/> earliest succeeded
    /// observations of <em>that revision</em> (the baseline) plus that revision's completed-observation count, and folds
    /// them via <see cref="DriftAnalysis.Analyze"/>. Every query is bounded, so the cost does not grow with canary age.
    ///
    /// <para>Scoping the baseline (and the observation count that drives warmup) to the latest run's pinned
    /// <c>PayloadRevision</c> is the fix for issue #89: a payload edit that adds or renames selectors — or an ad-hoc run
    /// at head mixed into the canary's stream — advances the pinned revision, so the baseline re-establishes against the
    /// new revision's own earliest healthy runs instead of freezing at the old revision's miss floor and reporting the
    /// new selectors as permanent false-positive drift. A revision change resets the state to <c>warmingUp</c>.</para></summary>
    [WolverineGet("/payloads/{id}/drift-status")]
    public static async Task<IResult> Handle(Guid id, IDocumentSession session, HttpContext http, CancellationToken ct)
    {
        // A pinned canary always references an existing payload (payloads are archived, never deleted); an unknown or
        // foreign id is simply absent in this tenant's partition — a 404, never a cross-tenant read.
        var payload = await session.Events.AggregateStreamAsync<Payload>(id, token: ct);
        if (payload is null)
        {
            return Results.NotFound();
        }

        var threshold = ParseThreshold(http);
        var baselineRuns = DriftAnalysis.DefaultBaselineRuns;

        // The current signal: the latest completed observation (a failed run is a completed observation — a strict/
        // required miss fails the run yet still records the missed selector; a cancelled/running/queued run is not). Its
        // pinned revision is the canary's CURRENT revision, which the baseline and count below are scoped to (#89).
        var latest = await session.Query<RunTimeline>()
            .Where(timeline => timeline.PayloadId == id && (timeline.Status == RunStatus.Succeeded || timeline.Status == RunStatus.Failed))
            .OrderByDescending(timeline => timeline.StartedAt)
            .Take(1)
            .ToListAsync(ct);

        IReadOnlyList<DriftObservation> baseline = [];
        DriftObservation? current = null;
        var observedRuns = 0;

        if (latest.Count > 0)
        {
            current = DriftObservation.FromTimeline(latest[0]);
            var revision = latest[0].PayloadRevision;

            // The baseline: the earliest healthy (succeeded) observations OF THE CURRENT REVISION establish its
            // steady-state miss floor. Scoping by revision resets the floor whenever the canary moves to a new revision.
            var baselineRows = await session.Query<RunTimeline>()
                .Where(timeline => timeline.PayloadId == id && timeline.PayloadRevision == revision && timeline.Status == RunStatus.Succeeded)
                .OrderBy(timeline => timeline.StartedAt)
                .Take(baselineRuns)
                .ToListAsync(ct);
            baseline = [.. baselineRows.Select(DriftObservation.FromTimeline)];

            // The completed-observation count, likewise scoped to the current revision — so observedRuns (and the warmup
            // it drives) reflects the revision under assessment, not the payload's whole cross-revision run history.
            observedRuns = await session.Query<RunTimeline>()
                .Where(timeline => timeline.PayloadId == id && timeline.PayloadRevision == revision && (timeline.Status == RunStatus.Succeeded || timeline.Status == RunStatus.Failed))
                .CountAsync(ct);
        }

        var status = DriftAnalysis.Analyze(id, payload.Name, baseline, current, observedRuns, baselineRuns, threshold);

        return Results.Ok(status);
    }

    /// <summary>Reads the optional per-payload alert threshold from <c>?threshold=N</c>: the count of newly-missing
    /// selectors tolerated before the status is <c>drifted</c>. Absent, non-numeric, or negative reads as 0 (any new
    /// miss drifts) rather than rejecting — a monitor's poll should never 400 on a stray query value.</summary>
    internal static int ParseThreshold(HttpContext http) =>
        http.Request.Query.TryGetValue("threshold", out var raw)
        && int.TryParse(raw.ToString(), CultureInfo.InvariantCulture, out var value)
        && value >= 0
            ? value
            : 0;
}
