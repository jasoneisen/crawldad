using System.Diagnostics.CodeAnalysis;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Crawldad.Web.Features.Runs;

/// <summary>The deferred run definition an enqueue persists (CD-16): exactly the fields <see cref="StartRun"/> is rebuilt from
/// at promotion, plus the input key names the <see cref="RunQueued"/> opening event records. Assembled by <c>POST /runs</c>
/// once it decides a run must queue, so the durable <see cref="QueuedRun"/> row carries everything the async executor needs
/// without the originating request.</summary>
/// <param name="RunId">The run/stream id.</param>
/// <param name="PayloadName">The payload's logical name.</param>
/// <param name="ScriptHash">The executed script's hash (drift/audit).</param>
/// <param name="Script">The payload JSON (already credential-scrubbed and executable).</param>
/// <param name="Inputs">The run inputs JSON (credentials are by-reference only, so this is safe to persist).</param>
/// <param name="InputKeys">The supplied input key names (never values, §12) — recorded on the opening event.</param>
/// <param name="PayloadId">The pinned managed payload, or null for an inline run.</param>
/// <param name="PayloadRevision">The pinned revision, or null for an inline run.</param>
/// <param name="DeadlineMs">The run wall-clock cap in milliseconds (§8.4), scheduled at promotion, not enqueue.</param>
public sealed record QueuedRunRequest(
    Guid RunId,
    string PayloadName,
    string ScriptHash,
    string Script,
    string Inputs,
    IReadOnlyList<string> InputKeys,
    Guid? PayloadId,
    int? PayloadRevision,
    int DeadlineMs);

/// <summary>
/// The tenant's durable FIFO admission queue (CD-16, docs/PRODUCT.md §Pv.3): the queue-at-cap alternative to a 429. A run
/// accepted at the concurrent-run cap is <b>enqueued</b> (a <see cref="QueuedRun"/> row + a <see cref="RunQueued"/> stream +
/// a <c>queued</c> <see cref="RunProgress"/>) instead of rejected; when a slot frees, the tenant's oldest queued run is
/// <b>promoted</b> (a <see cref="RunDequeued"/> transition + a <see cref="StartRun"/> kick through the unchanged executor
/// path). This service owns the queue's small in-memory state — the process-monotonic FIFO <see cref="NextSequence"/> counter
/// (seeded across restarts, so ordering is clock-independent and restart-durable) — and the Marten reads/writes the endpoints
/// and the <see cref="PromoteQueued"/>/<see cref="QueueWaitDeadline"/> handlers funnel through.
/// <para>
/// <b>Never-exceed-the-cap &amp; no-double-promote.</b> Promotion reserves the slot with the gate's atomic
/// <see cref="IRunAdmissionGate.TryAdmit"/> (which both bounds occupancy by the cap <em>and</em> refuses a run it already
/// counts), so concurrent promotion triggers can neither over-admit nor promote the same run twice — no promotion lock is
/// needed. It opens its own Marten session and manages the slot in a try/catch (mirroring the executor), releasing the
/// reservation if the durable commit fails so a failed promotion leaves the run cleanly queued for the next trigger. Like the
/// admission gate the slot counts are per-process (CD-3): a multi-instance deployment can transiently over-admit until runs
/// finalise — the single-instance-authoritative trade-off CD-3 documents, unchanged here.
/// </para>
/// </summary>
/// <param name="store">The Marten store (promotion and the wait-timeout own their sessions, off the request transaction).</param>
/// <param name="gate">The concurrent-run admission gate — the atomic slot reservation promotion funnels through.</param>
/// <param name="signals">The in-process SSE tail-wakeup hub, pinged on the queued→running transition.</param>
/// <param name="tenants">The tenant directory — the source of a tenant's per-tenant queue-depth override (CD-1).</param>
/// <param name="limits">The bound resource-limit options — the global default queue depth + max queue wait.</param>
/// <param name="clock">The time seam for enqueue/promotion timestamps and the queue-wait measurement.</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "RunQueue models the run admission queue; 'Queue' is the CD-16 domain term, not a Queue<T>-derived collection.")]
public sealed class RunQueue(
    IDocumentStore store,
    IRunAdmissionGate gate,
    RunEventSignals signals,
    TenantRegistry tenants,
    IOptions<RunLimitsOptions> limits,
    TimeProvider clock)
{
    /// <summary>The typed 429 code when a tenant's admission queue is already at its per-tier depth (CD-16) — the only 429 from
    /// admission now that the concurrent-run cap queues rather than rejects.</summary>
    public const string QueueDepthExceededCode = "queue_depth_exceeded";

    /// <summary>The typed terminal failure code for a run that waited in the queue past the max-queue-wait bound (CD-16).</summary>
    public const string QueueWaitExceededCode = "queue_wait_exceeded";

    private readonly int _defaultDepth = limits.Value.MaxQueueDepthPerTenant;
    private readonly int _maxWaitMs = limits.Value.MaxQueueWaitMs;
    private long _sequence;

    /// <summary>The next FIFO ordering key — a process-monotonic counter (never a wall-clock time: the test clock is frozen and
    /// two enqueues can share an instant). Distinct and strictly increasing across concurrent enqueues.</summary>
    /// <returns>The next sequence value.</returns>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Seeds the FIFO counter above a restart's surviving high-water mark so post-restart enqueues never reuse or
    /// undercut a queued run's sequence — the mechanism that keeps FIFO ordering correct across a process restart. Idempotent
    /// and monotonic (never lowers the counter).</summary>
    /// <param name="highWaterSequence">The maximum <see cref="QueuedRun.Sequence"/> that survived the restart.</param>
    public void Seed(long highWaterSequence)
    {
        long current;
        while ((current = Interlocked.Read(ref _sequence)) < highWaterSequence)
        {
            Interlocked.CompareExchange(ref _sequence, highWaterSequence, current);
        }
    }

    /// <summary>The tenant's queue-depth cap: its per-tenant override (CD-1) or the global default. At the cap a further at-cap
    /// run is rejected <c>429 queue_depth_exceeded</c> rather than enqueued.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <returns>The tenant's max queue depth.</returns>
    public int QueueDepthFor(string tenantId) =>
        tenants.TryGetQueueDepthOverride(tenantId, out var over) ? over : _defaultDepth;

    /// <summary>Whether the tenant already has any run waiting in its queue. New arrivals consult this so a fresh run cannot
    /// jump ahead of already-waiting runs when a slot is momentarily free — strict FIFO, no starvation of the queue.</summary>
    /// <param name="session">The tenant-scoped session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>True when at least one <see cref="QueuedRun"/> exists for the tenant.</returns>
    public Task<bool> HasQueuedAsync(IQuerySession session, CancellationToken ct) =>
        session.Query<QueuedRun>().AnyAsync(ct);

    /// <summary>The tenant's current queue depth (the count of its <see cref="QueuedRun"/> rows).</summary>
    /// <param name="session">The tenant-scoped session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The number of runs currently queued for the tenant.</returns>
    public Task<int> DepthAsync(IQuerySession session, CancellationToken ct) =>
        session.Query<QueuedRun>().CountAsync(ct);

    /// <summary>A run's 1-based position in its tenant's FIFO queue, computed on read as the count of queued runs ahead of it
    /// (a smaller sequence) plus one — never a denormalised counter, so it simply decreases as earlier runs promote. Null when
    /// the run is no longer queued (it was promoted/cancelled in the read race), so the caller omits a stale position.</summary>
    /// <param name="session">The tenant-scoped session.</param>
    /// <param name="runId">The run to locate.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The 1-based position, or null when the run is not (or no longer) queued.</returns>
    public async Task<int?> PositionAsync(IQuerySession session, Guid runId, CancellationToken ct)
    {
        var mine = await session.LoadAsync<QueuedRun>(runId, ct);
        if (mine is null)
        {
            return null;
        }

        var ahead = await session.Query<QueuedRun>().CountAsync(q => q.Sequence < mine.Sequence, ct);
        return ahead + 1;
    }

    /// <summary>Enqueues an at-cap run durably (CD-16): opens its stream with a scrubbed <see cref="RunQueued"/> opener, stores
    /// the deferred definition (<see cref="QueuedRun"/>) and a <c>queued</c> <see cref="RunProgress"/> in one transaction, then
    /// (when a max-queue-wait bound is configured) schedules its <see cref="QueueWaitDeadline"/>. Returns the run's 1-based
    /// queue position for the <c>202</c> body. The caller's session is already tenant-scoped (CD-1).</summary>
    /// <param name="session">The request's tenant-scoped Marten session.</param>
    /// <param name="bus">The bus the queue-wait timeout is scheduled on (durable, so it survives a restart).</param>
    /// <param name="request">The deferred run definition.</param>
    /// <param name="scrubber">Scrubs the opening event's credential-prone fields (§12).</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns>The run's 1-based queue position.</returns>
    public async Task<int> EnqueueAsync(IDocumentSession session, IMessageBus bus, QueuedRunRequest request, CredentialScrubber scrubber, CancellationToken ct)
    {
        var queuedAt = clock.GetUtcNow();
        var sequence = NextSequence();

        session.Events.StartStream<Run>(
            request.RunId,
            RunEventScrubber.Scrub(new RunQueued(request.PayloadName, request.ScriptHash, queuedAt, [.. request.InputKeys], request.PayloadId, request.PayloadRevision), scrubber));
        session.Store(new QueuedRun
        {
            Id = request.RunId,
            Sequence = sequence,
            PayloadName = request.PayloadName,
            ScriptHash = request.ScriptHash,
            Script = request.Script,
            Inputs = request.Inputs,
            PayloadId = request.PayloadId,
            PayloadRevision = request.PayloadRevision,
            DeadlineMs = request.DeadlineMs,
            QueuedAt = queuedAt,
        });
        session.Store(new RunProgress { Id = request.RunId, Status = RunStatus.Queued });
        await session.SaveChangesAsync(ct);

        if (_maxWaitMs > 0)
        {
            // Bound time-in-queue (CD-16): a durable scheduled message that terminates the run with queue_wait_exceeded if it
            // is still queued when it fires. Scheduled after the enqueue commits; Wolverine persists it, so it survives a restart.
            await bus.PublishAsync(new QueueWaitDeadline(request.RunId), new DeliveryOptions { ScheduleDelay = TimeSpan.FromMilliseconds(_maxWaitMs), TenantId = session.TenantId });
        }

        return await session.Query<QueuedRun>().CountAsync(q => q.Sequence <= sequence, ct);
    }

    /// <summary>Cancels a run while it is still queued (CD-16): dequeues it and drives it to its <see cref="RunCancelled"/>
    /// terminal state without ever consuming a slot (so nothing is promoted). A no-op returning false when the run is not (or
    /// no longer) queued.</summary>
    /// <param name="session">The request's tenant-scoped Marten session.</param>
    /// <param name="progress">The run's already-loaded progress (its status is re-checked under this write).</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns>True when the queued run was dequeued and cancelled.</returns>
    public async Task<bool> CancelQueuedAsync(IDocumentSession session, RunProgress progress, CancellationToken ct)
    {
        if (progress.Status != RunStatus.Queued)
        {
            return false;
        }

        session.Events.Append(progress.Id, new RunCancelled(EmptyStats, clock.GetUtcNow()));
        progress.Status = RunStatus.Cancelled;
        session.Delete<QueuedRun>(progress.Id);
        session.Store(progress);
        await session.SaveChangesAsync(ct);
        signals.Notify(progress.Id);
        return true;
    }

    /// <summary>Promotes the tenant's oldest queued run into a freed slot, if one is queued and a slot is free (CD-16): reserves
    /// the slot atomically via the gate, appends the <see cref="RunDequeued"/> transition, flips its progress to <c>running</c>
    /// with the realised queue wait, deletes the queue row, and kicks <see cref="StartRun"/> (which schedules the wall-clock
    /// deadline from here). Serialisation-free correctness: the gate's atomic reserve both bounds the cap and refuses a run it
    /// already counts, so concurrent triggers never over-admit or double-promote. Returns whether it promoted a run, so the
    /// caller re-triggers to drain the next free slot.</summary>
    /// <param name="bus">The bus <see cref="StartRun"/> is published on.</param>
    /// <param name="tenantId">The tenant whose queue to drain (from the trigger's envelope).</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>True when a run was promoted (drain the next); false when nothing was queued or no slot was free.</returns>
    public async Task<bool> PromoteOldestAsync(IMessageBus bus, string tenantId, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        var oldest = await session.Query<QueuedRun>().OrderBy(q => q.Sequence).FirstOrDefaultAsync(ct);
        if (oldest is null)
        {
            return false; // nothing queued
        }

        if (!gate.TryAdmit(tenantId, oldest.Id))
        {
            return false; // no free slot (or already reserved by a concurrent promotion) — leave it queued for the next trigger
        }

        try
        {
            var startedAt = clock.GetUtcNow();
            var waitMs = (long)(startedAt - oldest.QueuedAt).TotalMilliseconds;

            session.Events.Append(oldest.Id, new RunDequeued(startedAt, waitMs));
            var progress = (await session.LoadAsync<RunProgress>(oldest.Id, ct))!;
            progress.Status = RunStatus.Running;
            progress.QueueWaitMs = waitMs;
            session.Store(progress);
            session.Delete<QueuedRun>(oldest.Id);
            await session.SaveChangesAsync(ct);
        }
        catch
        {
            gate.Release(tenantId, oldest.Id); // undo the reservation if the durable commit failed — the run stays queued
            throw;
        }

        // Kick the executor saga exactly as an immediate async run does — the wall-clock deadline (§8.4) is scheduled from HERE
        // (promotion), so time spent queued never counts against it. A crash between the commit above and this publish is the
        // same rare orphan window the immediate async start already carries; restart recovery re-drives running runs.
        await bus.PublishAsync(new StartRun(oldest.Id, oldest.PayloadName, oldest.ScriptHash, oldest.Script, oldest.Inputs, oldest.PayloadId, oldest.PayloadRevision, oldest.DeadlineMs));
        signals.Notify(oldest.Id); // a tailing SSE client sees the queued→running transition live
        return true;
    }

    /// <summary>Stats for a run that never executed (queued-cancel / queue-wait-timeout): all zero.</summary>
    internal static RunStats EmptyStats { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>The durable trigger to promote a tenant's oldest queued run when a slot frees (CD-16): published after any run
/// reaches a terminal state (the executor's finalisation, a synchronous run's completion, or a queued-run cancellation), and
/// re-published by its own handler to drain further free slots. The tenant rides the Wolverine envelope (CD-1); a durable
/// local-queue message, so a trigger survives a restart and the queue self-heals.</summary>
public sealed record PromoteQueued;

/// <summary>The max-queue-wait timeout for one queued run (CD-16): a durable <b>scheduled</b> message published at enqueue and
/// routed back after the bound elapses. If the run is still queued it is terminated cleanly with <c>queue_wait_exceeded</c>;
/// if it already promoted/cancelled the timeout is spent harmlessly. The tenant rides the envelope.</summary>
/// <param name="RunId">The queued run to time out.</param>
public sealed record QueueWaitDeadline(Guid RunId);

/// <summary>The durable-queue handler that promotes queued runs when a slot frees (CD-16): a thin shell over
/// <see cref="RunQueue.PromoteOldestAsync"/> that re-publishes itself to drain each further free slot in FIFO order. It injects
/// the bus (not a request session) — the queue service owns its Marten session — so no request transaction wraps it.</summary>
public static class PromoteQueuedHandler
{
    /// <summary>Promotes one queued run for the message's tenant and, if it promoted one, re-triggers to drain the next.</summary>
    /// <param name="_">The trigger (routing only; the tenant is on the envelope).</param>
    /// <param name="queue">The run queue.</param>
    /// <param name="bus">The bus for <see cref="StartRun"/> and the drain re-trigger.</param>
    /// <param name="envelope">The message envelope — its tenant id scopes the promotion (CD-1).</param>
    /// <param name="ct">The handler cancellation token.</param>
    public static async Task Handle(PromoteQueued _, RunQueue queue, IMessageBus bus, Envelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelope.TenantId))
        {
            return; // a trigger without a tenant cannot resolve a queue (CD-1) — fail closed
        }

        if (await queue.PromoteOldestAsync(bus, envelope.TenantId, ct))
        {
            await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = envelope.TenantId });
        }
    }
}

/// <summary>The durable-queue handler for a queued run's max-wait timeout (CD-16): terminates a still-queued run with
/// <c>queue_wait_exceeded</c>. It owns its Marten session (injecting the store, not a request session), so no request
/// transaction wraps it, and it promotes nothing — a run expiring in the queue held no slot.</summary>
public static class QueueWaitDeadlineHandler
{
    /// <summary>Times out one queued run for the message's tenant, or no-ops if it already left the queue.</summary>
    /// <param name="command">The run to time out.</param>
    /// <param name="store">The Marten store (the handler owns its own tenant-scoped session).</param>
    /// <param name="envelope">The message envelope — its tenant id scopes the session (CD-1).</param>
    /// <param name="signals">The SSE tail-wakeup hub, pinged so a tailing client sees the terminal failure live.</param>
    /// <param name="clock">The time seam for the terminal event timestamp.</param>
    /// <param name="ct">The handler cancellation token.</param>
    public static async Task Handle(QueueWaitDeadline command, IDocumentStore store, Envelope envelope, RunEventSignals signals, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelope.TenantId))
        {
            return; // no tenant — fail closed (never touch the default partition)
        }

        await using var session = store.LightweightSession(envelope.TenantId);
        var progress = await session.LoadAsync<RunProgress>(command.RunId, ct);
        if (progress is not { Status: RunStatus.Queued })
        {
            return; // already promoted, cancelled, or gone — the timeout is spent harmlessly (idempotent)
        }

        var failure = new RunFailureDetail("terminal", RunQueue.QueueWaitExceededCode, "the run exceeded its maximum queue wait (CD-16)", new RunStepRef(0, "queue"));
        session.Events.Append(command.RunId, new RunFailed(failure, RunQueue.EmptyStats, clock.GetUtcNow()));
        progress.Status = RunStatus.Failed;
        progress.Failure = failure;
        session.Delete<QueuedRun>(command.RunId);
        session.Store(progress);
        await session.SaveChangesAsync(ct);
        signals.Notify(command.RunId);
    }
}
