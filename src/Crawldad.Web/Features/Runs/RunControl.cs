using System.Collections.Concurrent;

namespace Crawldad.Web.Features.Runs;

/// <summary>Why a run was asked to stop cooperatively, so the executor maps the interpreter's stopped outcome to the right
/// disposition: a user <see cref="Cancelled"/> reports <c>cancelled</c> + partial; a <see cref="Deadline"/> breach is a
/// terminal failure.</summary>
public enum RunStopReason
{
    /// <summary>A caller requested cancellation via <c>POST /runs/{id}/cancel</c>.</summary>
    Cancelled,

    /// <summary>The run's wall-clock deadline elapsed (the saga timeout).</summary>
    Deadline,
}

/// <summary>The in-process stop signal for one executing run: the cancel endpoint and the saga's wall-clock timeout set
/// it; the executor's observer reads it between steps. Fast and lock-free — the durable record of a cancel is the
/// <c>RunCancellationRequested</c> trace event, not this. First writer wins, so a late deadline never overrides an in-flight user cancel.</summary>
public sealed class RunControl
{
    private const int _notStopped = -1;
    private int _reason = _notStopped;
    private int _claimed;
    private CancellationTokenSource? _forcible;
    private bool _forcibleForEveryReason;

    /// <summary>Atomically claims the run for the calling executor, returning true for the first caller only. Prevents two
    /// executors in a process (a durable redelivery and the startup recovery scan) from driving the same run at once.</summary>
    public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    /// <summary>Whether a stop (cancel or deadline) has been requested — read by the interpreter between steps.</summary>
    public bool StopRequested => Volatile.Read(ref _reason) != _notStopped;

    /// <summary>The reason the run was stopped, or null if it has not been stopped.</summary>
    public RunStopReason? StopReason
    {
        get
        {
            var reason = Volatile.Read(ref _reason);
            return reason == _notStopped ? null : (RunStopReason)reason;
        }
    }

    /// <summary>Binds the deadline-cancellation source so a <see cref="RunStopReason.Deadline"/> stop can forcibly interrupt
    /// a blocked run — a user <see cref="RunStopReason.Cancelled"/> stays cooperative unless <paramref name="forEveryReason"/>
    /// is true, which an observer-less auto-upgraded run needs since it never sees a cooperative cancel between steps.</summary>
    public void UseForcibleCancellation(CancellationTokenSource forcible, bool forEveryReason = false)
    {
        _forcible = forcible;
        _forcibleForEveryReason = forEveryReason;
    }

    /// <summary>Requests a cooperative stop for <paramref name="reason"/>; the first request wins (idempotent thereafter).
    /// A deadline (or any reason when bound forcible-for-every-reason) additionally cancels the bound source so a stuck
    /// run does not outrun its cap.</summary>
    public void Stop(RunStopReason reason)
    {
        if (Interlocked.CompareExchange(ref _reason, (int)reason, _notStopped) == _notStopped
            && (reason == RunStopReason.Deadline || _forcibleForEveryReason))
        {
            _forcible?.Cancel();
        }
    }
}

/// <summary>The registry of in-process <see cref="RunControl"/>s keyed by run id. A singleton shared by the executor
/// (which registers a run's control while it drives it) and the control surface (cancel endpoint, saga deadline).</summary>
public interface IRunControlRegistry
{
    /// <summary>Gets (creating if absent) the control for a run — used by the executor when it starts driving the run.</summary>
    RunControl GetOrAdd(Guid runId);

    /// <summary>Tries to get an existing control for a run, present only while the executor is actively driving it.</summary>
    bool TryGet(Guid runId, out RunControl control);

    /// <summary>Drops a run's control once the executor stops driving it (finalised or interrupted).</summary>
    void Remove(Guid runId);
}

/// <summary>The default concurrent-dictionary-backed <see cref="IRunControlRegistry"/>.</summary>
public sealed class RunControlRegistry : IRunControlRegistry
{
    private readonly ConcurrentDictionary<Guid, RunControl> _controls = new();

    /// <inheritdoc />
    public RunControl GetOrAdd(Guid runId) => _controls.GetOrAdd(runId, static _ => new RunControl());

    /// <inheritdoc />
    public bool TryGet(Guid runId, out RunControl control) => _controls.TryGetValue(runId, out control!);

    /// <inheritdoc />
    public void Remove(Guid runId) => _controls.TryRemove(runId, out _);
}
