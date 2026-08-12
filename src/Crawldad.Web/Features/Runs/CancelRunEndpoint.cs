using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary><c>POST /runs/{id}/cancel</c>: cancels a background run. A <b>running</b> run gets a cooperative cancel — the
/// executor honours it between steps and the run reaches <c>cancelled</c> with a partial result. A <b>queued</b> run is
/// dequeued straight to <c>cancelled</c> without consuming a slot, so nothing is promoted.</summary>
public static class CancelRunEndpoint
{
    /// <summary>Handles <c>POST /runs/{id}/cancel</c>.</summary>
    [WolverinePost("/runs/{id}/cancel")]
    public static async Task<IResult> Handle(
        Guid id,
        IDocumentSession session,
        IRunControlRegistry controls,
        IMessageBus bus,
        // [FromServices]: this POST has no request body, so without the marker Wolverine would treat the first complex
        // parameter as the body to deserialize (a 400 for RunQueue, which has no parameterless ctor). Both are resolved services.
        [FromServices] RunQueue queue,
        [FromServices] RunEventSignals signals,
        TimeProvider clock,
        CancellationToken ct)
    {
        var progress = await session.LoadAsync<RunProgress>(id, ct);
        if (progress is null)
        {
            return Results.NotFound();
        }

        // Snapshot the pre-cancel state (its queue position, if queued) for the acknowledgement before any mutation.
        var position = progress.Status == RunStatus.Queued ? await queue.PositionAsync(session, id, ct) : null;
        var acknowledged = GetRunEndpoint.ToResponse(progress, position);

        if (progress.Status == RunStatus.Running)
        {
            session.Events.Append(id, new RunCancellationRequested(clock.GetUtcNow()));
            await session.SaveChangesAsync(ct);
            controls.GetOrAdd(id).Stop(RunStopReason.Cancelled);
            signals.Notify(id);
        }
        else if (progress.Status == RunStatus.Queued)
        {
            // Cancel-while-queued: dequeue and drive to cancelled under the run stream's exclusive lock. The run held no
            // slot, so nothing is freed or promoted. A lost claim (the run promoted in the race between load and claim)
            // leaves the now-running run alone — the caller may re-cancel it.
            await queue.CancelQueuedAsync(session.TenantId!, id, ct);

            // Notify downstream subscribers (webhook fan-out) that the queued run reached terminal. Off the execution
            // path; the subscriber reads the committed run state, so a lost-claim publish simply finds no terminal event.
            await bus.PublishAsync(new RunFinalized(id), new DeliveryOptions { TenantId = session.TenantId });
        }

        return Results.Accepted($"/runs/{id}", acknowledged);
    }
}
