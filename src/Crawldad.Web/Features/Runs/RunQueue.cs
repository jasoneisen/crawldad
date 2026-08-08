using System.Collections.Concurrent;
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
/// path).
/// <para>
/// <b>Exactly one terminal writer per queued run.</b> A queued run has three competing next-state writers on separate
/// sessions — promotion (the <see cref="PromoteQueued"/> handler), cancel-while-queued (the HTTP thread), and the
/// <see cref="QueueWaitDeadline"/> timeout. They are made mutually exclusive at the data layer by
/// <see cref="TryClaimTerminalAsync"/>, which serialises them on the run stream's Postgres advisory lock
/// (<c>AppendExclusive</c>) and, <em>under the lock</em>, re-reads the authoritative <see cref="RunProgress"/> and commits its
/// transition <b>iff the run is still queued</b> — so the first to win appends its terminal event and the others abort having
/// written nothing. This restores the pre-CD-16 single-terminal-writer invariant (no double-terminal trace, no leaked slot, no
/// zombie run). Promotion additionally reserves its slot with the gate's atomic <see cref="IRunAdmissionGate.TryAdmit"/> and
/// releases it if it loses the claim.
/// </para>
/// <para>
/// <b>FIFO ordering is restart-durable and collision-proof.</b> Sequences come from a per-tenant counter seeded lazily on its
/// first use from the surviving high-water <see cref="QueuedRun.Sequence"/> — <em>before</em> any value is assigned, so a
/// boot-window enqueue can never undercut a restored queue (no dependency on a startup-ordering seed). Slot counts stay
/// per-process (CD-3): a multi-instance deployment can transiently over-admit until runs finalise — the documented trade-off.
/// </para>
/// </summary>
/// <param name="store">The Marten store (promotion, cancel, and the wait-timeout own their sessions, off the request transaction).</param>
/// <param name="gate">The concurrent-run admission gate — the atomic slot reservation promotion funnels through.</param>
/// <param name="signals">The in-process SSE tail-wakeup hub, pinged on a queued run's transition.</param>
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

    // The per-tenant FIFO counter, seeded lazily (below) from the durable high-water mark on first use. Per tenant so a
    // tenant-scoped seed query is authoritative; kept in-memory so assignment is a lock-free Interlocked increment.
    private readonly ConcurrentDictionary<string, Sequence> _sequences = new(StringComparer.Ordinal);

    /// <summary>The next FIFO ordering key for a tenant — a process-monotonic counter (never a wall-clock time: the test clock
    /// is frozen and two enqueues can share an instant). Seeded once per tenant, <b>before assigning any value</b>, from the
    /// surviving high-water <see cref="QueuedRun.Sequence"/> so a post-restart (or boot-window) enqueue can never take a
    /// colliding low sequence — the collision-proof FIFO-across-restart guarantee.</summary>
    /// <param name="session">The enqueue's tenant-scoped session (the seed query runs on it).</param>
    /// <param name="tenantId">The run's tenant.</param>
    /// <param name="ct">Cancels the seed query.</param>
    /// <returns>The next sequence value (strictly above every surviving queued run's).</returns>
    public async Task<long> NextSequenceAsync(IQuerySession session, string tenantId, CancellationToken ct)
    {
        var sequence = _sequences.GetOrAdd(tenantId, static _ => new Sequence());

        // Seed only while still at 0 (unseeded). The compare-exchange seeds at most once even under concurrent first-enqueues
        // and never resets a value already advanced by another thread's increment, so no double-checked lock is needed.
        if (Interlocked.Read(ref sequence.Value) == 0)
        {
            var highWater = await session.Query<QueuedRun>()
                .OrderByDescending(q => q.Sequence)
                .Select(q => q.Sequence)
                .FirstOrDefaultAsync(ct);
            Interlocked.CompareExchange(ref sequence.Value, highWater, 0);
        }

        return Interlocked.Increment(ref sequence.Value);
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
    /// (when a max-queue-wait bound is configured) schedules its <see cref="QueueWaitDeadline"/> and triggers a promotion
    /// attempt so a run enqueued just as the last slot frees is not stranded behind idle capacity. Returns the run's 1-based
    /// queue position for the <c>202</c> body. The caller's session is already tenant-scoped (CD-1).</summary>
    /// <param name="session">The request's tenant-scoped Marten session.</param>
    /// <param name="bus">The bus the queue-wait timeout and promotion trigger are published on (durable, surviving a restart).</param>
    /// <param name="request">The deferred run definition.</param>
    /// <param name="scrubber">Scrubs the opening event's credential-prone fields (§12).</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns>The run's 1-based queue position.</returns>
    public async Task<int> EnqueueAsync(IDocumentSession session, IMessageBus bus, QueuedRunRequest request, CredentialScrubber scrubber, CancellationToken ct)
    {
        var queuedAt = clock.GetUtcNow();
        var sequence = await NextSequenceAsync(session, session.TenantId!, ct);

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

    /// <summary>Cancels a run while it is still queued (CD-16): drives it to its <see cref="RunCancelled"/> terminal state
    /// without ever consuming a slot (so nothing is promoted), under the run stream's exclusive lock so it cannot double-write
    /// with a racing promotion/timeout. Returns false when it lost the claim (a concurrent promotion/timeout already
    /// transitioned the run) — the caller leaves the now-running/terminal run alone.</summary>
    /// <param name="tenantId">The run's tenant.</param>
    /// <param name="runId">The run to cancel.</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns>True when this call cancelled the still-queued run; false when it lost the claim.</returns>
    public Task<bool> CancelQueuedAsync(string tenantId, Guid runId, CancellationToken ct) =>
        TryClaimTerminalAsync(tenantId, runId, new RunCancelled(EmptyStats, clock.GetUtcNow()), static p => p.Status = RunStatus.Cancelled, ct);

    /// <summary>Times out a run that has waited past its max queue wait (CD-16): drives it to a terminal <c>queue_wait_exceeded</c>
    /// failure under the run stream's exclusive lock, or no-ops (returns false) when it already left the queue.</summary>
    /// <param name="tenantId">The run's tenant.</param>
    /// <param name="runId">The run to time out.</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns>True when this call failed the still-queued run; false when it had already left the queue.</returns>
    public Task<bool> TimeoutQueuedAsync(string tenantId, Guid runId, CancellationToken ct)
    {
        var failure = new RunFailureDetail("terminal", QueueWaitExceededCode, "the run exceeded its maximum queue wait (CD-16)", new RunStepRef(0, "queue"));
        return TryClaimTerminalAsync(tenantId, runId, new RunFailed(failure, EmptyStats, clock.GetUtcNow()), p =>
        {
            p.Status = RunStatus.Failed;
            p.Failure = failure;
        }, ct);
    }

    /// <summary>Promotes the tenant's oldest queued run into a freed slot, if one is queued and a slot is free (CD-16): reserves
    /// the slot atomically via the gate, then claims the run's single transition under its stream lock — appending
    /// <see cref="RunDequeued"/>, flipping its progress to <c>running</c> with the realised queue wait, deleting the queue row,
    /// and kicking <see cref="StartRun"/> (which schedules the wall-clock deadline from here). If it loses that claim to a
    /// concurrent cancel/timeout it releases the reserved slot. Returns whether it promoted a run, so the caller re-triggers to
    /// drain the next free slot.</summary>
    /// <param name="bus">The bus <see cref="StartRun"/> is published on.</param>
    /// <param name="tenantId">The tenant whose queue to drain (from the trigger's envelope).</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>True when the caller should re-drain — a run was promoted (fill the next free slot) OR the claim was lost and the
    /// reserved slot is free again (promote the next queued run into it); false when nothing was queued or no slot was free.</returns>
    public async Task<bool> PromoteOldestAsync(IMessageBus bus, string tenantId, CancellationToken ct)
    {
        QueuedRun? oldest;
        await using (var read = store.QuerySession(tenantId))
        {
            oldest = await read.Query<QueuedRun>().OrderBy(q => q.Sequence).FirstOrDefaultAsync(ct);
        }

        if (oldest is null)
        {
            return false; // nothing queued — no drain
        }

        if (!gate.TryAdmit(tenantId, oldest.Id))
        {
            return false; // no free slot (or already reserved by a concurrent promotion) — leave it queued for the next trigger
        }

        // The slot is reserved. Every non-win exit — a lost claim OR a throw — MUST release it, else it leaks permanently (a
        // retry's TryAdmit refuses the already-counted run). A lost claim frees the slot, so the caller re-drains the next run.
        try
        {
            var startedAt = clock.GetUtcNow();
            var waitMs = (long)(startedAt - oldest.QueuedAt).TotalMilliseconds;
            var won = await TryClaimTerminalAsync(tenantId, oldest.Id, new RunDequeued(startedAt, waitMs), p =>
            {
                p.Status = RunStatus.Running;
                p.QueueWaitMs = waitMs;
            }, ct);

            if (won)
            {
                // Kick the executor saga exactly as an immediate async run does — the wall-clock deadline (§8.4) is scheduled
                // from HERE (promotion), so time spent queued never counts against it.
                await bus.PublishAsync(new StartRun(oldest.Id, oldest.PayloadName, oldest.ScriptHash, oldest.Script, oldest.Inputs, oldest.PayloadId, oldest.PayloadRevision, oldest.DeadlineMs));
            }
            else
            {
                gate.Release(tenantId, oldest.Id); // a concurrent cancel/timeout claimed it first — free the reserved slot
            }
        }
        catch
        {
            gate.Release(tenantId, oldest.Id); // the claim or the StartRun publish threw — free the reserved slot before the retry
            throw;
        }

        // A promotion attempt ran: the reserved slot is now held by a promoted run (fill the NEXT free slot) or freed again by
        // a lost claim (whose winner deleted the queue row, so the next drain sees the following run). Re-drain either way.
        return true;
    }

    // The single mutual-exclusion point for a queued run's competing terminal/next-state writers (promotion, cancel,
    // wait-timeout). AppendExclusive takes the run stream's Postgres advisory lock — held until this session saves — serialising
    // the three writers; under the lock the authoritative RunProgress is re-read, and the transition is committed IFF the run is
    // still queued. A caller that finds the run already left the queue commits nothing (dispose discards the queued append and
    // releases the lock), so exactly one writer wins: no double-terminal trace, no leaked slot, no zombie run. RunProgress is
    // created at enqueue and only ever updated (never deleted), so it is present under the lock.
    private async Task<bool> TryClaimTerminalAsync(string tenantId, Guid runId, object transition, Action<RunProgress> applyWon, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        await session.Events.AppendExclusive(runId, ct, transition);
        var progress = (await session.LoadAsync<RunProgress>(runId, ct))!;
        if (progress.Status != RunStatus.Queued)
        {
            return false; // a concurrent promotion/cancel/timeout already claimed this run — commit nothing
        }

        applyWon(progress);
        session.Store(progress);
        session.Delete<QueuedRun>(runId);
        await session.SaveChangesAsync(ct);
        signals.Notify(runId); // a tailing SSE client sees the transition / terminal live
        return true;
    }

    /// <summary>Stats for a run that never executed (queued-cancel / queue-wait-timeout): all zero.</summary>
    internal static RunStats EmptyStats { get; } = new(0, 0, 0, 0, 0);

    // A per-tenant FIFO counter cell, mutated only through Interlocked so assignment is lock-free.
    private sealed class Sequence
    {
        public long Value;
    }
}

/// <summary>The durable trigger to promote a tenant's oldest queued run when capacity may exist (CD-16): published after any
/// run reaches a terminal state (the executor's finalisation, a synchronous run's completion), after an enqueue (so a run that
/// arrives just as the last slot frees is not stranded), and re-published by its own handler to drain further free slots. The
/// tenant rides the Wolverine envelope (CD-1); a durable local-queue message, so a trigger survives a restart and the queue
/// self-heals.</summary>
public sealed record PromoteQueued;

/// <summary>The max-queue-wait timeout for one queued run (CD-16): a durable <b>scheduled</b> message published at enqueue and
/// routed back after the bound elapses. If the run is still queued it is terminated cleanly with <c>queue_wait_exceeded</c>;
/// if it already promoted/cancelled the timeout is spent harmlessly. The tenant rides the envelope.</summary>
/// <param name="RunId">The queued run to time out.</param>
public sealed record QueueWaitDeadline(Guid RunId);

/// <summary>The durable-queue handler that promotes queued runs when capacity may exist (CD-16): a thin shell over
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
/// <c>queue_wait_exceeded</c> (under the run stream's exclusive lock, so it cannot race a promotion/cancel), or no-ops if it
/// already left the queue. It promotes nothing — a run expiring in the queue held no slot.</summary>
public static class QueueWaitDeadlineHandler
{
    /// <summary>Times out one queued run for the message's tenant, or no-ops if it already left the queue.</summary>
    /// <param name="command">The run to time out.</param>
    /// <param name="queue">The run queue (owns the tenant-scoped session and the exclusive-claim).</param>
    /// <param name="envelope">The message envelope — its tenant id scopes the claim (CD-1).</param>
    /// <param name="ct">The handler cancellation token.</param>
    public static async Task Handle(QueueWaitDeadline command, RunQueue queue, Envelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelope.TenantId))
        {
            return; // no tenant — fail closed (never touch the default partition)
        }

        await queue.TimeoutQueuedAsync(envelope.TenantId, command.RunId, ct);
    }
}
