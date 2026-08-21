using Crawldad.Contracts.Runs;
using JasperFx.Events;
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
        // parameter as the body to deserialize (a 400 for RunQueue, which has no parameterless ctor). All are resolved services.
        [FromServices] RunQueue queue,
        [FromServices] RunEventSignals signals,
        [FromServices] IDocumentStore store,
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
            // Signal the in-process executor to stop FIRST: it stops appending trace events to this run's stream and
            // converges to terminal quickly, bounding the version contention the durable record below has to ride out.
            controls.GetOrAdd(id).Stop(RunStopReason.Cancelled);

            // Then durably record the request. The executor is a lock-free (plain-Append) writer on this SAME stream, so a
            // naive append here can lose a stream-version race — Marten surfaces that as EventStreamUnexpectedMaxEventIdException,
            // an unhandled 500 that silently drops the cancel (issue #108). RecordCancellationRequestedAsync retries under
            // optimistic concurrency, re-reading the run each attempt so it never annotates a run that finalised in between.
            await RecordCancellationRequestedAsync(store, session.TenantId!, id, clock, ct);
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

    // Appends the durable RunCancellationRequested breadcrumb, resilient to the executor concurrently advancing the same
    // run stream. AppendOptimistic reads the stream version up front and Marten guards it at commit, so a lost race throws
    // EventStreamUnexpectedMaxEventIdException rather than corrupting the stream — we swallow it and retry from a fresh
    // read (a fresh session each attempt, since a failed commit poisons the session). Every attempt re-loads the run and
    // stops if it is no longer running: the run may have finalised between attempts, and its executor's own terminal
    // RunCancelled already records the outcome, so an already-terminal run is never re-annotated. The loop terminates
    // because the executor appends only finitely many events before it finalises (after which the re-read sees terminal),
    // and once the caller's stop flag halts the executor a subsequent attempt wins uncontended.
    private static async Task RecordCancellationRequestedAsync(IDocumentStore store, string tenantId, Guid runId, TimeProvider clock, CancellationToken ct)
    {
        while (true)
        {
            await using var session = store.LightweightSession(tenantId);

            // Loading the run implies its stream (a running run is never erased — DELETE /runs/{id} 409s a non-terminal run),
            // so the row is present; the ! mirrors the executor's own load-then-finalise sites.
            var progress = (await session.LoadAsync<RunProgress>(runId, ct))!;
            if (progress.Status != RunStatus.Running)
            {
                return;
            }

            try
            {
                await session.Events.AppendOptimistic(runId, ct, new RunCancellationRequested(clock.GetUtcNow()));
                await session.SaveChangesAsync(ct);
                return;
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                // The executor committed an append between our version read and our commit — re-read and retry.
            }
        }
    }
}
