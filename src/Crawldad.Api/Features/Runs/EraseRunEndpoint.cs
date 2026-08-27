using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Runs;

/// <summary><c>DELETE /runs/{id}</c>: on-demand erasure of a <b>finished</b> run — the tenant's right-to-erasure path for
/// a run whose result carried PII (issue #71). Tenant-scoped like every run endpoint. Hard-deletes, in one transaction,
/// the run's stored result (<see cref="RunProgress"/>), its derived read models (the <see cref="Run"/> snapshot and the
/// <see cref="RunTimeline"/>), and its event stream — erasing both the bulk result body and the incidental PII a scrubbed
/// timeline can still hold (a <c>LogEmitted</c> message, a <c>Navigated</c> URL).
///
/// <para><c>204</c> when a terminal run was erased; <c>404</c> when this tenant has no such run — an unknown, foreign,
/// already-erased, or purely-synchronous run (which persists no progress row) — so there is no existence oracle and a
/// repeated DELETE is idempotent (<c>204</c> then <c>404</c>, matching <c>DELETE /browsers/{name}</c>); <c>409</c>
/// <c>run_still_active</c> when the run is still <c>running</c>/<c>queued</c>, since a live run still has an executor (or a
/// queue entry) writing to it — cancel it first, then erase the settled run.</para></summary>
public static class EraseRunEndpoint
{
    /// <summary>The typed conflict code when a DELETE targets a run that has not reached a terminal disposition.</summary>
    public const string RunStillActiveCode = "run_still_active";

    /// <summary>Handles <c>DELETE /runs/{id}</c>. Injects both the request's tenant-scoped <see cref="IDocumentSession"/>
    /// (the erasure's unit of work) and the singleton <see cref="IDocumentStore"/> (read only for the event schema name);
    /// both are interfaces Wolverine resolves as services, never a request body.</summary>
    [WolverineDelete("/runs/{id}")]
    public static async Task<IResult> Handle(Guid id, IDocumentSession session, IDocumentStore store, CancellationToken ct)
    {
        var progress = await session.LoadAsync<RunProgress>(id, ct);
        if (progress is null)
        {
            return Results.NotFound(); // unknown / another tenant's / already-erased / purely-synchronous run — no oracle
        }

        if (progress.Status is RunStatus.Running or RunStatus.Queued)
        {
            // A live run still has its executor writing progress (or a queue entry + scheduled promotion) — erasing it now
            // would race that writer and could corrupt the admission queue. Require a cancel first; the settled run is erasable.
            var state = progress.Status == RunStatus.Running ? "running" : "queued"; // the two non-terminal statuses, wire-cased
            return Results.Json(
                new RunRejection(RunStillActiveCode, $"run {id} is still {state}; cancel it before deleting"),
                statusCode: StatusCodes.Status409Conflict);
        }

        // Terminal: erase the result document and both derived read models, and hard-delete the event stream, all in one
        // tenant-scoped transaction — so GET /runs/{id}, /timeline, /drift, and /events all 404 afterwards, coherently.
        session.Delete(progress);
        session.Delete<Run>(id);
        session.Delete<RunTimeline>(id);
        EraseEventStream(session, store, id);
        await session.SaveChangesAsync(ct);

        // 204 with no body — the erased content is never echoed, and no event is appended (nothing to leak into a trace).
        return Results.NoContent();
    }

    // Hard-deletes the run's Marten event stream (its events + the stream row) for the session's tenant, enlisted in the
    // SAME unit of work as the document deletes so the whole erasure commits atomically. Marten exposes only a SOFT
    // ArchiveStream (events physically remain, is_archived=true), which does not satisfy erasure — so the stream rows are
    // removed directly via tenant-scoped SQL on the session's batch (QueueSqlCommand). The events-before-stream order
    // respects the mt_events → mt_streams foreign key; the tenant_id predicate is defence in depth (the session is already
    // tenant-scoped), and the schema is the configured events schema, never caller input.
    private static void EraseEventStream(IDocumentSession session, IDocumentStore store, Guid streamId)
    {
        var schema = store.Options.Events.DatabaseSchemaName;
        var tenantId = session.TenantId!;
        session.QueueSqlCommand($"delete from {schema}.mt_events where stream_id = ? and tenant_id = ?", streamId, tenantId);
        session.QueueSqlCommand($"delete from {schema}.mt_streams where id = ? and tenant_id = ?", streamId, tenantId);
    }
}
