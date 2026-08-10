using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Crawldad.Web.Features.Runs;

/// <summary>The deferred run definition an enqueue persists: exactly the fields <see cref="StartRun"/> is rebuilt from
/// at promotion, plus the input key names the <see cref="RunQueued"/> opening event records. Assembled by <c>POST /runs</c>
/// once it decides a run must queue, so <see cref="QueuedRun"/> carries everything the executor needs without the request.</summary>
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

/// <summary>The tenant's durable FIFO admission queue: the queue-at-cap alternative to a 429. A run at the concurrent-run
/// cap is enqueued instead of rejected; when a slot frees, the oldest queued run is promoted. Exactly one terminal writer
/// wins per queued run — promotion/cancel/timeout serialise via <see cref="TryClaimTerminalAsync"/>'s advisory lock + re-read-under-lock, so the losers commit nothing.</summary>
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
    /// <summary>The typed 429 code when a tenant's admission queue is already at its per-tier depth — the only 429 from
    /// admission now that the concurrent-run cap queues rather than rejects.</summary>
    public const string QueueDepthExceededCode = "queue_depth_exceeded";

    /// <summary>The typed terminal failure code for a run that waited in the queue past the max-queue-wait bound.</summary>
    public const string QueueWaitExceededCode = "queue_wait_exceeded";

    private readonly int _defaultDepth = limits.Value.MaxQueueDepthPerTenant;
    private readonly int _maxWaitMs = limits.Value.MaxQueueWaitMs;

    // The per-tenant FIFO counter, seeded lazily (below) from the durable high-water mark on first use. Per tenant so a
    // tenant-scoped seed query is authoritative; kept in-memory so assignment is a lock-free Interlocked increment.
    private readonly ConcurrentDictionary<string, Sequence> _sequences = new(StringComparer.Ordinal);

    /// <summary>The next FIFO ordering key for a tenant — a process-monotonic counter (never a wall-clock time, since the
    /// test clock is frozen and two enqueues can share an instant). Seeded once per tenant, <b>before assigning any
    /// value</b>, from the surviving high-water <see cref="QueuedRun.Sequence"/>, so a post-restart enqueue can never take a colliding low sequence.</summary>
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

    /// <summary>The tenant's queue-depth cap: its per-tenant override or the global default. At the cap a further
    /// at-cap run is rejected <c>429 queue_depth_exceeded</c> rather than enqueued.</summary>
    public int QueueDepthFor(string tenantId) =>
        tenants.TryGetQueueDepthOverride(tenantId, out var over) ? over : _defaultDepth;

    /// <summary>Whether the tenant already has any run waiting in its queue. New arrivals consult this so a fresh run cannot
    /// jump ahead of already-waiting runs when a slot is momentarily free — strict FIFO, no starvation of the queue.</summary>
    public Task<bool> HasQueuedAsync(IQuerySession session, CancellationToken ct) =>
        session.Query<QueuedRun>().AnyAsync(ct);

    /// <summary>The tenant's current queue depth (the count of its <see cref="QueuedRun"/> rows).</summary>
    public Task<int> DepthAsync(IQuerySession session, CancellationToken ct) =>
        session.Query<QueuedRun>().CountAsync(ct);

    /// <summary>A run's 1-based position in its tenant's FIFO queue, computed on read as the count of queued runs ahead
    /// of it (a smaller sequence) plus one — never a denormalised counter. Null when the run is no longer queued (it was
    /// promoted/cancelled in the read race), so the caller omits a stale position.</summary>
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

    /// <summary>Enqueues an at-cap run durably: opens its stream with a scrubbed <see cref="RunQueued"/> opener and stores
    /// the deferred definition + a <c>queued</c> <see cref="RunProgress"/> in one transaction, then (if a max-wait bound is
    /// configured) schedules its <see cref="QueueWaitDeadline"/> and nudges a promotion so it is not stranded behind idle capacity.</summary>
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
            // Bound time-in-queue: a durable scheduled message that terminates the run with queue_wait_exceeded if it is
            // still queued when it fires. Scheduled after the enqueue commits; Wolverine persists it, so it survives a restart.
            await bus.PublishAsync(new QueueWaitDeadline(request.RunId), new DeliveryOptions { ScheduleDelay = TimeSpan.FromMilliseconds(_maxWaitMs), TenantId = session.TenantId });
        }

        return await session.Query<QueuedRun>().CountAsync(q => q.Sequence <= sequence, ct);
    }

    /// <summary>Cancels a run while it is still queued: drives it to <see cref="RunCancelled"/> without ever consuming a
    /// slot, under the run stream's exclusive lock so it cannot double-write with a racing promotion/timeout. False when
    /// it lost the claim (a concurrent promotion/timeout already transitioned the run).</summary>
    public Task<bool> CancelQueuedAsync(string tenantId, Guid runId, CancellationToken ct) =>
        TryClaimTerminalAsync(tenantId, runId, new RunCancelled(EmptyStats, clock.GetUtcNow()), static p => p.Status = RunStatus.Cancelled, ct);

    /// <summary>Times out a run that has waited past its max queue wait: drives it to a terminal
    /// <c>queue_wait_exceeded</c> failure under the exclusive lock, or no-ops when it already left the queue.</summary>
    public Task<bool> TimeoutQueuedAsync(string tenantId, Guid runId, CancellationToken ct)
    {
        var failure = new RunFailureDetail("terminal", QueueWaitExceededCode, "the run exceeded its maximum queue wait (CD-16)", new RunStepRef(0, "queue"));
        return TryClaimTerminalAsync(tenantId, runId, new RunFailed(failure, EmptyStats, clock.GetUtcNow()), p =>
        {
            p.Status = RunStatus.Failed;
            p.Failure = failure;
        }, ct);
    }

    /// <summary>Promotes the tenant's oldest queued run into a freed slot, if one is queued and a slot is free: reserves the
    /// slot atomically via the gate, then claims the run's single transition under its stream lock (appending
    /// <see cref="RunDequeued"/> and kicking <see cref="StartRun"/>), releasing the reserved slot if it loses that claim.</summary>
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
                // Kick the executor saga exactly as an immediate async run does — the wall-clock deadline is scheduled
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

    // The single mutual-exclusion point for a queued run's competing writers (promotion, cancel, wait-timeout).
    // AppendExclusive holds the run stream's Postgres advisory lock while it re-reads RunProgress and commits the
    // transition IFF still queued — so exactly one writer wins; the rest find it already left the queue and commit nothing.
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

/// <summary>The durable trigger to promote a tenant's oldest queued run when capacity may exist: published after any run
/// reaches terminal, after an enqueue (so a run arriving just as the last slot frees is not stranded), and re-published
/// by its own handler to drain further free slots. A durable local-queue message, so it survives a restart.</summary>
public sealed record PromoteQueued;

/// <summary>The max-queue-wait timeout for one queued run: a durable <b>scheduled</b> message published at enqueue and
/// routed back after the bound elapses. If still queued it is terminated cleanly with <c>queue_wait_exceeded</c>; if it
/// already promoted/cancelled the timeout is spent harmlessly.</summary>
public sealed record QueueWaitDeadline(Guid RunId);

/// <summary>The durable-queue handler that promotes queued runs when capacity may exist: a thin shell over
/// <see cref="RunQueue.PromoteOldestAsync"/> that re-publishes itself to drain each further free slot in FIFO order. It
/// injects the bus (not a request session), so no request transaction wraps it.</summary>
public static class PromoteQueuedHandler
{
    /// <summary>Promotes one queued run for the message's tenant and, if it promoted one, re-triggers to drain the next.</summary>
    public static async Task Handle(PromoteQueued _, RunQueue queue, IMessageBus bus, Envelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelope.TenantId))
        {
            return; // a trigger without a tenant cannot resolve a queue — fail closed
        }

        if (await queue.PromoteOldestAsync(bus, envelope.TenantId, ct))
        {
            await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = envelope.TenantId });
        }
    }
}

/// <summary>The durable-queue handler for a queued run's max-wait timeout: terminates a still-queued run with
/// <c>queue_wait_exceeded</c> under the run stream's exclusive lock, or no-ops if it already left the queue. It
/// promotes nothing — a run expiring in the queue held no slot.</summary>
public static class QueueWaitDeadlineHandler
{
    /// <summary>Times out one queued run for the message's tenant, or no-ops if it already left the queue.</summary>
    public static async Task Handle(QueueWaitDeadline command, RunQueue queue, Envelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelope.TenantId))
        {
            return; // no tenant — fail closed (never touch the default partition)
        }

        await queue.TimeoutQueuedAsync(envelope.TenantId, command.RunId, ct);
    }
}
