using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>POST /runs/{id}/cancel</c> (§11): requests a cooperative cancel of a running background run. It appends the durable
/// <c>RunCancellationRequested</c> trace event and raises the in-process stop signal; the executor honours it <b>between
/// steps</b>, tears the backend session down cleanly, and the run reaches <c>cancelled</c> with a partial result (poll
/// <c>GET /runs/{id}</c>). Cancelling a run that has already finished is a no-op; the signal is set through
/// <see cref="IRunControlRegistry.GetOrAdd"/> so a cancel that lands during a resume window is still observed when the run
/// picks back up.
/// </summary>
public static class CancelRunEndpoint
{
    /// <summary>Handles <c>POST /runs/{id}/cancel</c>.</summary>
    /// <param name="id">The run to cancel.</param>
    /// <param name="session">The Marten session (appends the cancellation event).</param>
    /// <param name="controls">The in-process run-control registry.</param>
    /// <param name="signals">The SSE notification hub, pinged so a tailing client sees the cancellation request live.</param>
    /// <param name="clock">The time seam for the event timestamp.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns><c>202</c> with the current <see cref="RunStateResponse"/>, or <c>404</c> when there is no such run.</returns>
    [WolverinePost("/runs/{id}/cancel")]
    public static async Task<IResult> Handle(Guid id, IDocumentSession session, IRunControlRegistry controls, RunEventSignals signals, TimeProvider clock, CancellationToken ct)
    {
        var progress = await session.LoadAsync<RunProgress>(id, ct);
        if (progress is null)
        {
            return Results.NotFound();
        }

        if (progress.Status == RunStatus.Running)
        {
            session.Events.Append(id, new RunCancellationRequested(clock.GetUtcNow()));
            await session.SaveChangesAsync(ct);
            controls.GetOrAdd(id).Stop(RunStopReason.Cancelled);
            signals.Notify(id);
        }

        return Results.Accepted($"/runs/{id}", GetRunEndpoint.ToResponse(progress));
    }
}
