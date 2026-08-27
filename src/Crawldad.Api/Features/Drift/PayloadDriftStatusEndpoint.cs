using System.Globalization;
using Crawldad.Api.Features.Payloads;
using Crawldad.Api.Features.Runs;
using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Drift;

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
    /// them via <see cref="DriftAnalysis.Analyze"/>. Each query returns a bounded result, but only the baseline is
    /// bounded <em>work</em> (an index range scan of at most <see cref="DriftAnalysis.DefaultBaselineRuns"/> rows); the
    /// latest-run and count queries ride the same index restricted to the payload's (resp. the revision's) rows, so their
    /// work is proportional to that history rather than a full-table scan — not strictly age-independent.
    ///
    /// <para>Scoping the baseline (and the observation count that drives warmup) to the latest run's pinned
    /// <c>PayloadRevision</c> is the fix for issue #89: a payload edit that adds or renames selectors — or an ad-hoc run
    /// at head mixed into the canary's stream — advances the pinned revision, so the baseline re-establishes against the
    /// new revision's own earliest healthy runs instead of freezing at the old revision's miss floor and reporting the
    /// new selectors as permanent false-positive drift. A revision the canary has not yet run
    /// <see cref="DriftAnalysis.DefaultBaselineRuns"/>+1 healthy times reads as <c>warmingUp</c>; a rollback or re-pin to
    /// an <em>already-baselined</em> revision instead resumes <c>steady</c>/<c>drifted</c> immediately against that
    /// revision's own established floor.</para>
    ///
    /// <para>The current revision is whichever the <em>latest completed</em> run pinned, so when runs of two revisions
    /// interleave — e.g. an ad-hoc head run landing between the canary's own — a single poll can report the other
    /// revision and transiently mask a real drift on the pinned one; the next canary run at the pinned revision
    /// self-corrects it. This is accepted deliberately: drift is a slow, polled signal, and pinning to the latest
    /// observation keeps the read a fixed set of bounded-result queries with no cross-revision merge.</para></summary>
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
        // pinned revision is the canary's CURRENT revision, which the baseline and count below are scoped to (#89). When
        // two revisions interleave (an ad-hoc head run mixed into the canary stream) this can flip the assessed revision
        // for one poll — a transient, self-correcting mask we accept over a cross-revision merge (see the summary).
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
