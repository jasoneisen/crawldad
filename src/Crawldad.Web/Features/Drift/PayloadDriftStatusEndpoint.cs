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
    /// <summary>Handles <c>GET /payloads/{id}/drift-status</c>. Reads at most <see cref="DriftAnalysis.DefaultBaselineRuns"/>
    /// earliest succeeded observations (the baseline) plus the latest completed observation for the payload, then folds
    /// them via <see cref="DriftAnalysis.Analyze"/>. Every query is bounded, so the cost does not grow with canary age.</summary>
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

        // The baseline: the earliest healthy (succeeded) observations establish the steady-state miss floor.
        var baseline = await session.Query<RunTimeline>()
            .Where(timeline => timeline.PayloadId == id && timeline.Status == RunStatus.Succeeded)
            .OrderBy(timeline => timeline.StartedAt)
            .Take(baselineRuns)
            .ToListAsync(ct);

        // The current signal: the latest completed observation (a failed run is a completed observation — a strict/
        // required miss fails the run yet still records the missed selector; a cancelled/running/queued run is not).
        var latest = await session.Query<RunTimeline>()
            .Where(timeline => timeline.PayloadId == id && (timeline.Status == RunStatus.Succeeded || timeline.Status == RunStatus.Failed))
            .OrderByDescending(timeline => timeline.StartedAt)
            .Take(1)
            .ToListAsync(ct);

        var observedRuns = await session.Query<RunTimeline>()
            .Where(timeline => timeline.PayloadId == id && (timeline.Status == RunStatus.Succeeded || timeline.Status == RunStatus.Failed))
            .CountAsync(ct);

        var status = DriftAnalysis.Analyze(
            id,
            payload.Name,
            [.. baseline.Select(DriftObservation.FromTimeline)],
            latest.Count > 0 ? DriftObservation.FromTimeline(latest[0]) : null,
            observedRuns,
            baselineRuns,
            threshold);

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
