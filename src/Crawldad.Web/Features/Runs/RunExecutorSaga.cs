using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Crawldad.Web.Features.Runs;

/// <summary>Starts the durable executor saga (§11/§14.2): carries the resolved, scrubbed run definition so the background
/// executor — and any resume after a restart — can re-establish the run without the originating HTTP request. Sent by the
/// <c>async</c> <c>POST /runs</c> path once it has pinned <c>RunStarted</c> and seeded the running <see cref="RunProgress"/>.</summary>
/// <param name="RunId">The run/saga id.</param>
/// <param name="PayloadName">The payload's logical name.</param>
/// <param name="ScriptHash">The executed script's hash (drift/audit).</param>
/// <param name="Script">The payload document JSON (already credential-scrubbed and executable).</param>
/// <param name="Inputs">The run inputs JSON (credentials are by-reference only, so this is safe to persist).</param>
/// <param name="PayloadId">The pinned managed payload, or null for an inline run.</param>
/// <param name="PayloadRevision">The pinned revision, or null for an inline run.</param>
/// <param name="DeadlineMs">The run wall-clock cap in milliseconds (§8.4), scheduled as the saga timeout.</param>
public sealed record StartRun(
    Guid RunId,
    string PayloadName,
    string ScriptHash,
    string Script,
    string Inputs,
    Guid? PayloadId,
    int? PayloadRevision,
    int DeadlineMs);

/// <summary>The durable local-queue message that drives one run to a terminal state (§11). Handled by the executor (which
/// owns its own Marten sessions, a departure from one-transaction-per-request); a process death before it is acked leaves
/// it in the durable inbox to be redelivered on restart, resuming the run from its last checkpoint.</summary>
/// <param name="RunId">The run to execute (or resume).</param>
public sealed record ExecuteRun(Guid RunId);

/// <summary>The run's wall-clock deadline (§8.4) as a Wolverine saga timeout: scheduled by <see cref="RunExecutorSaga.Start"/>
/// and, when it elapses, routed back to the saga (auto-ignored if the saga is already gone). Marked with the saga id so it
/// correlates to its run.</summary>
/// <param name="RunId">The run/saga id.</param>
/// <param name="DeadlineDelay">How long after start the deadline fires.</param>
public sealed record RunDeadline([property: SagaIdentity] Guid RunId, TimeSpan DeadlineDelay) : TimeoutMessage(DeadlineDelay);

/// <summary>
/// The run executor saga (§11/§14.2), the net-new durable-orchestration piece. Marten-backed saga storage (automatic via
/// the host's <c>IntegrateWithWolverine()</c>) holds the immutable run definition; the mutable execution state — checkpoint,
/// status, result — lives in the executor-owned <see cref="RunProgress"/> document so the long-running executor's own-session
/// writes never contend with Wolverine's saga persistence. <see cref="Start"/> pins the run and kicks the durable
/// <see cref="ExecuteRun"/> plus the wall-clock <see cref="RunDeadline"/>; <see cref="Handle(RunDeadline, IRunControlRegistry)"/>
/// enforces the deadline by asking the in-process control to stop the run (a terminal failure, §8.4). Because messaging is
/// durable, the orchestration survives process restarts and the run resumes from its last checkpoint.
/// </summary>
public sealed class RunExecutorSaga : Saga
{
    /// <summary>The run/saga id (the Marten document id).</summary>
    public Guid Id { get; set; }

    /// <summary>The payload's logical name (observability).</summary>
    public string PayloadName { get; set; } = "";

    /// <summary>The executed script's hash (drift/audit).</summary>
    public string ScriptHash { get; set; } = "";

    /// <summary>The pinned managed payload, or null for an inline run.</summary>
    public Guid? PayloadId { get; set; }

    /// <summary>The pinned revision, or null for an inline run.</summary>
    public int? PayloadRevision { get; set; }

    /// <summary>The payload document JSON (scrubbed, executable) — re-run source for a resumed run.</summary>
    public string Script { get; set; } = "";

    /// <summary>The run inputs JSON — re-connect source for a resumed run (credentials are by-reference only).</summary>
    public string Inputs { get; set; } = "";

    /// <summary>Starts the saga: pins the definition and kicks the durable executor plus the wall-clock deadline timeout.</summary>
    /// <param name="command">The resolved run definition.</param>
    /// <returns>The new saga and its cascading messages.</returns>
    public static (RunExecutorSaga, OutgoingMessages) Start(StartRun command)
    {
        var saga = new RunExecutorSaga
        {
            Id = command.RunId,
            PayloadName = command.PayloadName,
            ScriptHash = command.ScriptHash,
            PayloadId = command.PayloadId,
            PayloadRevision = command.PayloadRevision,
            Script = command.Script,
            Inputs = command.Inputs,
        };

        var outgoing = new OutgoingMessages
        {
            new ExecuteRun(command.RunId),
            new RunDeadline(command.RunId, TimeSpan.FromMilliseconds(command.DeadlineMs)),
        };

        return (saga, outgoing);
    }

    /// <summary>Enforces the wall-clock deadline (§8.4): if the run is still being driven, ask its control to stop with a
    /// deadline reason (the executor then finalises a terminal <c>run_deadline_exceeded</c> failure). If the run already
    /// finished, its control is gone and this is a no-op (the timeout is spent harmlessly).</summary>
    /// <param name="_">The elapsed deadline (routing only).</param>
    /// <param name="controls">The in-process run-control registry.</param>
    public void Handle(RunDeadline _, IRunControlRegistry controls)
    {
        if (controls.TryGet(Id, out var control))
        {
            control.Stop(RunStopReason.Deadline);
        }
    }
}
