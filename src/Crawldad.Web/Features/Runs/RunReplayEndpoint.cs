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

/// <summary><c>POST /runs/{id}/replay</c>: re-executes a historical run's <b>pinned payload revision</b> — the exact
/// script, guaranteeing drift-comparable results. Inputs are resupplied by the caller (values are never persisted); only
/// a pinned run is replayable — an inline run is rejected with a typed <see cref="RunRejection"/> (<c>inline_not_replayable</c>).</summary>
public static class RunReplayEndpoint
{
    /// <summary>Handles <c>POST /runs/{id}/replay</c>.</summary>
    [WolverinePost("/runs/{id}/replay")]
    public static async Task<IResult> Handle(
        Guid id,
        ReplayRunRequest request,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        ISecretStoreRegistry secretStores,
        IMessageBus bus,
        IRunAdmissionGate gate,
        RunQueue queue,
        IRunControlRegistry controls,
        SyncRunSupervisor supervisor,
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
        return await StartRunEndpoint.Handle(start, session, registry, sinks, scrubber, secretScope, secretStores, bus, gate, queue, controls, supervisor, limitsOptions, clock, ct);
    }
}
