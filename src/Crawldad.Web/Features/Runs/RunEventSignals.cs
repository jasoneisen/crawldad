using System.Collections.Concurrent;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The low-latency SSE tail signal for one run (§11): a wakeup, not a payload. The authoritative frame source is the run's
/// durable Marten stream (read-your-writes, so no frame is ever lost or duplicated across a reconnect); this only lets the
/// SSE endpoint wake the instant a new event is appended instead of polling. <see cref="Changed"/> is captured <b>before</b>
/// a re-read, so a <see cref="Notify"/> that lands during the read still completes the awaited task and the next wait returns
/// at once — a missed wakeup only defers to the endpoint's poll backstop, never drops an event (the durable re-read is the
/// correctness guarantee). One process, in-memory, lock-free.
/// </summary>
public sealed class RunSignal
{
    private volatile TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the next <see cref="Notify"/>. Capture it before re-reading the stream, then await it.</summary>
    public Task Changed => _changed.Task;

    /// <summary>Signals that a new event was appended: completes the current <see cref="Changed"/> and arms a fresh one.</summary>
    public void Notify() =>
        Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}

/// <summary>
/// The registry of per-run <see cref="RunSignal"/>s (§11/§13), a singleton shared by the executor (which
/// <see cref="Notify"/>s after every durable append — step events, checkpoints, resume, terminal) and the SSE endpoint
/// (which <see cref="For"/>s a run to subscribe). <see cref="Notify"/> is cheap and creates nothing when no one is watching,
/// so the executor's many appends cost nothing until an SSE client connects; the connecting client always backfills from the
/// durable stream first, so events appended before it subscribed are never missed. The executor <see cref="Remove"/>s a run's
/// slot when it stops driving it.
/// </summary>
public sealed class RunEventSignals
{
    private readonly ConcurrentDictionary<Guid, RunSignal> _signals = new();

    /// <summary>Gets (creating if absent) the signal for a run — used by the SSE endpoint to subscribe.</summary>
    /// <param name="runId">The run id.</param>
    public RunSignal For(Guid runId) => _signals.GetOrAdd(runId, static _ => new RunSignal());

    /// <summary>Wakes any subscriber for a run. A no-op when no one is watching (nothing is allocated).</summary>
    /// <param name="runId">The run whose stream just grew.</param>
    public void Notify(Guid runId)
    {
        if (_signals.TryGetValue(runId, out var signal))
        {
            signal.Notify();
        }
    }

    /// <summary>Drops a run's signal slot once the executor stops driving it (an active subscriber keeps its own reference).</summary>
    /// <param name="runId">The run id.</param>
    public void Remove(Guid runId) => _signals.TryRemove(runId, out _);
}
