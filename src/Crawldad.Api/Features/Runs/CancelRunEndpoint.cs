using Crawldad.Contracts.Runs;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine;
using Wolverine.Http;

namespace Crawldad.Api.Features.Runs;

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
            // Durably record the request FIRST, then signal the stop. The order is load-bearing: an auto-upgraded
            // (sync->async) run binds its control forcible-for-EVERY-reason (StartRunEndpoint), so Stop() below does not just
            // set a cooperative flag — it forcibly unblocks the observer-less interpreter and launches the supervisor's
            // finaliser, which appends the terminal event with a plain, un-retried Append (RunFinalization) that has no
            // Wolverine retry behind it. Recording before Stop() lets our append land while that interpreter is still
            // blocked, so the finaliser reads a fresh version AFTER us and never races the record. The record itself is also
            // resilient to the OTHER writer — the normal async executor's live trace appends on the same stream (issue #108).
            await RecordCancellationRequestedAsync(store, session.TenantId!, id, clock, ct);
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

    // Durably appends the RunCancellationRequested breadcrumb, resilient to a concurrent lock-free append on the same run
    // stream (the async executor's live trace events; the sync-upgrade finaliser is ordered AFTER this by the caller, so it
    // never contends). AppendOptimistic pins the stream's expected version up front and Marten guards it at commit, so a
    // lost race throws EventStreamUnexpectedMaxEventIdException (Postgres MT003) rather than corrupting the stream — we
    // swallow it and retry from a fresh session (a failed commit poisons the session).
    //
    // Staging the append BEFORE re-reading RunProgress is deliberate — it pins the version first, so the terminal-status
    // read that follows is consistent with that pin: a terminal event committed by a self-finalising executor in the
    // read-to-commit window either already shows on the re-read (we skip) or advanced the stream past our pin (the guarded
    // save throws and we retry into the skip). So the breadcrumb is never appended behind a terminal event. The loop
    // terminates because those writers append only finitely many events before the run finalises (after which the re-read
    // sees terminal), and every await observes ct — a wedged process cannot spin it, ct bounds the wall-clock.
    private static async Task RecordCancellationRequestedAsync(IDocumentStore store, string tenantId, Guid runId, TimeProvider clock, CancellationToken ct)
    {
        while (true)
        {
            await using var session = store.LightweightSession(tenantId);

            // Pin the expected stream version now (staged, committed only by SaveChangesAsync below), before the re-read.
            await session.Events.AppendOptimistic(runId, ct, new RunCancellationRequested(clock.GetUtcNow()));

            // Loading the run implies its stream (a running run is never erased — DELETE /runs/{id} 409s a non-terminal run),
            // so the row is present; the ! mirrors the executor's own load-then-finalise sites.
            var progress = (await session.LoadAsync<RunProgress>(runId, ct))!;
            if (progress.Status != RunStatus.Running)
            {
                return; // finalised already — its executor/supervisor's own terminal event records the outcome; don't re-annotate
            }

            try
            {
                await session.SaveChangesAsync(ct);
                return;
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                // A concurrent lock-free append advanced the stream past our pinned version — re-read and retry.
            }
        }
    }
}
