using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>POST /runs/{id}/cancel</c> (§11/CD-16): cancels a background run. A <b>running</b> run gets a cooperative cancel — it
/// appends the durable <c>RunCancellationRequested</c> trace event and raises the in-process stop signal; the executor honours
/// it <b>between steps</b>, tears the backend session down cleanly, and the run reaches <c>cancelled</c> with a partial result
/// (poll <c>GET /runs/{id}</c>). A <b>queued</b> run (CD-16) is instead dequeued and driven straight to <c>cancelled</c>
/// <em>without ever consuming a slot</em>, so nothing is promoted. Cancelling a run that has already finished is a no-op; the
/// running signal is set through <see cref="IRunControlRegistry.GetOrAdd"/> so a cancel that lands during a resume window is
/// still observed when the run picks back up.
/// </summary>
public static class CancelRunEndpoint
{
    /// <summary>Handles <c>POST /runs/{id}/cancel</c>.</summary>
    /// <param name="id">The run to cancel.</param>
    /// <param name="session">The Marten session (appends the cancellation event / dequeues a queued run).</param>
    /// <param name="controls">The in-process run-control registry.</param>
    /// <param name="queue">The run queue — dequeues a run cancelled while still queued (CD-16).</param>
    /// <param name="signals">The SSE notification hub, pinged so a tailing client sees the cancellation live.</param>
    /// <param name="clock">The time seam for the event timestamp.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns><c>202</c> with the pre-cancel <see cref="RunStateResponse"/>, or <c>404</c> when there is no such run.</returns>
    [WolverinePost("/runs/{id}/cancel")]
    public static async Task<IResult> Handle(
        Guid id,
        IDocumentSession session,
        IRunControlRegistry controls,
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
            // Cancel-while-queued (CD-16): dequeue and drive to the cancelled terminal state. The run held no slot, so this
            // frees nothing and promotes nothing.
            await queue.CancelQueuedAsync(session, progress, ct);
        }

        return Results.Accepted($"/runs/{id}", acknowledged);
    }
}
