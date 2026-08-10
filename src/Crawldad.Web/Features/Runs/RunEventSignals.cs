using System.Collections.Concurrent;

namespace Crawldad.Web.Features.Runs;

/// <summary>The low-latency SSE tail signal for one run: a wakeup, not a payload. <see cref="Changed"/> is captured
/// <b>before</b> a re-read, so a <see cref="Notify"/> landing during the read still completes it — a missed wakeup only
/// defers to the poll backstop, never drops an event (the durable stream is the correctness guarantee). Lock-free.</summary>
public sealed class RunSignal
{
    private volatile TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the next <see cref="Notify"/>. Capture it before re-reading the stream, then await it.</summary>
    public Task Changed => _changed.Task;

    /// <summary>Signals that a new event was appended: completes the current <see cref="Changed"/> and arms a fresh one.</summary>
    public void Notify() =>
        Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}

/// <summary>The registry of per-run <see cref="RunSignal"/>s, a singleton shared by the executor (which notifies after
/// every durable append) and the SSE endpoint (which subscribes). <see cref="Notify"/> is a cheap no-op when nobody is
/// watching; a connecting client always backfills from the durable stream first, so nothing appended earlier is missed.</summary>
public sealed class RunEventSignals
{
    private readonly ConcurrentDictionary<Guid, RunSignal> _signals = new();

    /// <summary>Gets (creating if absent) the signal for a run — used by the SSE endpoint to subscribe.</summary>
    public RunSignal For(Guid runId) => _signals.GetOrAdd(runId, static _ => new RunSignal());

    /// <summary>Wakes any subscriber for a run. A no-op when no one is watching (nothing is allocated).</summary>
    public void Notify(Guid runId)
    {
        if (_signals.TryGetValue(runId, out var signal))
        {
            signal.Notify();
        }
    }

    /// <summary>Drops a run's signal slot once the executor stops driving it (an active subscriber keeps its own reference).</summary>
    public void Remove(Guid runId) => _signals.TryRemove(runId, out _);
}
