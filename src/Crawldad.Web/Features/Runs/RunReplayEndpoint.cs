using Crawldad.Contracts.Runs;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>POST /runs/{id}/replay</c> (§13 replay): re-executes a historical run's <b>pinned payload revision</b> (§14.1/§14.2).
/// It reads the run's pin (<c>payloadId</c> + <c>revision</c>) from its trace and re-runs that <b>exact</b> revision + script
/// hash, so the script is guaranteed identical — the basis of the drift story (compare a fresh run's trace to the original's
/// on the same revision → target-site drift; compare the pinned revision to the payload head → payload drift).
/// <para>
/// <b>Two deliberate v1 choices, documented here:</b> (1) <b>Inputs are resupplied by the caller.</b> §12 forbids persisting
/// input <em>values</em>, so a replay cannot recover the original inputs; the request carries fresh ones. The pinned revision
/// + hash guarantee the same <em>script</em> — the pragmatic replay contract. (2) <b>Only a pinned run is replayable.</b> An
/// inline run's script was never stored as a managed revision, so it cannot be re-fetched — such a run is rejected with a
/// typed <see cref="RunRejection"/> (<c>inline_not_replayable</c>). Pin resolution, the archived-payload guard, and the
/// sync/async dispatch are shared verbatim with <c>POST /runs</c> (so a replay of an archived payload is rejected exactly as
/// running it would be), and the response is the same shape: a <see cref="RunResponse"/> (sync) or a <c>202</c>
/// <see cref="RunStateResponse"/> (async).
/// </para>
/// </summary>
public static class RunReplayEndpoint
{
    /// <summary>Handles <c>POST /runs/{id}/replay</c>.</summary>
    /// <param name="id">The historical run to replay.</param>
    /// <param name="request">The resupplied inputs + async flag.</param>
    /// <param name="session">The request-scoped Marten session.</param>
    /// <param name="registry">Resolves the backend adapter named by the pinned payload's <c>config.backend</c>.</param>
    /// <param name="sinks">Resolves the download sink named by a <c>download.to</c> target (§9.3).</param>
    /// <param name="scrubber">Redacts credentials from every persisted event and the response (§12).</param>
    /// <param name="secretScope">The per-run secret registry the backend adapter registers the resolved credential into (§12).</param>
    /// <param name="bus">The message bus that kicks the executor saga for an async replay (§11).</param>
    /// <param name="clock">The time seam for event timestamps and duration.</param>
    /// <param name="ct">Cancels the replay.</param>
    /// <returns><c>200</c>/<c>202</c> as <c>POST /runs</c>, <c>404</c> for an unknown run, or <c>400</c> for an inline (non-replayable) run.</returns>
    [WolverinePost("/runs/{id}/replay")]
    public static async Task<IResult> Handle(
        Guid id,
        ReplayRunRequest request,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        IMessageBus bus,
        IOptions<RunLimitsOptions> limitsOptions,
        TimeProvider clock,
        CancellationToken ct)
    {
        var run = await session.Events.AggregateStreamAsync<Run>(id, token: ct);
        if (run is null)
        {
            return Results.NotFound();
        }

        if (run.PayloadId is not Guid payloadId)
        {
            return Results.BadRequest(new RunRejection(
                "inline_not_replayable",
                $"run '{id}' executed an inline payload, which is not replayable — only a run pinned to a managed payload can be replayed"));
        }

        // Re-run the EXACT pinned revision (never the head) with the caller-resupplied inputs, delegating to POST /runs so
        // pin resolution + the archived guard + sync/async dispatch stay a single implementation.
        var start = new StartRunRequest(default, request.Inputs, payloadId, run.PayloadRevision, request.Async);
        return await StartRunEndpoint.Handle(start, session, registry, sinks, scrubber, secretScope, bus, limitsOptions, clock, ct);
    }
}
