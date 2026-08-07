using System.Collections.Concurrent;

namespace Crawldad.Web.Features.Runs;

/// <summary>Why a run was asked to stop cooperatively (§11), so the executor maps the interpreter's stopped outcome to the
/// right disposition: a user <see cref="Cancelled"/> reports <c>cancelled</c> + partial; a <see cref="Deadline"/> breach is
/// a terminal failure (§8.4).</summary>
public enum RunStopReason
{
    /// <summary>A caller requested cancellation via <c>POST /runs/{id}/cancel</c>.</summary>
    Cancelled,

    /// <summary>The run's wall-clock deadline elapsed (the saga timeout, §8.4).</summary>
    Deadline,
}

/// <summary>
/// The in-process stop signal for one executing run (§11): the cancel endpoint and the saga's wall-clock timeout set it;
/// the executor's run observer reads it between steps. It is a fast, lock-free, single-process control (the executor and
/// the control surface share a process in the solo-mode host) — the <em>persistent</em> record of a cancel is the
/// <c>RunCancellationRequested</c> trace event, not this. First writer wins so a late deadline never overrides a user
/// cancel already in flight.
/// </summary>
public sealed class RunControl
{
    private const int _notStopped = -1;
    private int _reason = _notStopped;
    private int _claimed;
    private CancellationTokenSource? _forcible;

    /// <summary>Atomically claims the run for the calling executor, returning true for the first caller only. Prevents two
    /// executors in a process (a durable redelivery and the startup recovery scan) from driving the same run at once (§11).</summary>
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

    /// <summary>The executor binds its deadline-cancellation source so a <see cref="RunStopReason.Deadline"/> stop can
    /// forcibly interrupt a run blocked mid-call (§8.4) — a user <see cref="RunStopReason.Cancelled"/> stop stays
    /// cooperative, honoured between steps and never yanked mid-step (§11).</summary>
    /// <param name="forcible">The linked cancellation source the executor's run observes.</param>
    public void UseForcibleCancellation(CancellationTokenSource forcible) => _forcible = forcible;

    /// <summary>Requests a cooperative stop for <paramref name="reason"/>; the first request wins (idempotent thereafter).
    /// A deadline additionally cancels the bound forcible source so a stuck run does not outrun its cap.</summary>
    /// <param name="reason">Why the run should stop.</param>
    public void Stop(RunStopReason reason)
    {
        if (Interlocked.CompareExchange(ref _reason, (int)reason, _notStopped) == _notStopped && reason == RunStopReason.Deadline)
        {
            _forcible?.Cancel();
        }
    }
}

/// <summary>The registry of in-process <see cref="RunControl"/>s keyed by run id (§11). A singleton shared by the executor
/// (which registers a run's control while it drives it) and the control surface (cancel endpoint, saga deadline).</summary>
public interface IRunControlRegistry
{
    /// <summary>Gets (creating if absent) the control for a run — used by the executor when it starts driving the run.</summary>
    /// <param name="runId">The run id.</param>
    RunControl GetOrAdd(Guid runId);

    /// <summary>Tries to get an existing control for a run, present only while the executor is actively driving it.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="control">The control, when present.</param>
    bool TryGet(Guid runId, out RunControl control);

    /// <summary>Drops a run's control once the executor stops driving it (finalised or interrupted).</summary>
    /// <param name="runId">The run id.</param>
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
