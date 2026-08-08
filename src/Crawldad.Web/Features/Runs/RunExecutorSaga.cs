using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Crawldad.Web.Features.Runs;

/// <summary>Starts the durable executor saga (§11/§14.2): carries the resolved, scrubbed run definition so the background
/// executor — and any resume after a restart — can re-establish the run without the originating HTTP request. Sent by the
/// <c>async</c> <c>POST /runs</c> path once it has pinned <c>RunStarted</c> and seeded the running <see cref="RunProgress"/>,
/// by the CD-15 sync auto-upgrade, and by CD-16 promotion. The run id is the <see cref="SagaIdentity"/>, so a redelivered or
/// duplicate <c>StartRun</c> for the same run correlates to the existing saga and is a no-op (see
/// <see cref="RunExecutorSaga.StartOrHandle"/>) rather than a second saga or a duplicate-key error.</summary>
/// <param name="RunId">The run/saga id.</param>
/// <param name="PayloadName">The payload's logical name.</param>
/// <param name="ScriptHash">The executed script's hash (drift/audit).</param>
/// <param name="Script">The payload document JSON (already credential-scrubbed and executable).</param>
/// <param name="Inputs">The run inputs JSON (credentials are by-reference only, so this is safe to persist).</param>
/// <param name="PayloadId">The pinned managed payload, or null for an inline run.</param>
/// <param name="PayloadRevision">The pinned revision, or null for an inline run.</param>
/// <param name="DeadlineMs">The run wall-clock cap in milliseconds (§8.4), scheduled as the deadline delay.</param>
public sealed record StartRun(
    [property: SagaIdentity] Guid RunId,
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

/// <summary>The run's wall-clock deadline (§8.4): a durable <b>scheduled</b> message the saga's <see cref="RunExecutorSaga.StartOrHandle"/>
/// delays by the run's <c>deadlineMs</c>. It is deliberately <b>not</b> a saga timeout — its only job is to ask the still-running
/// run's in-process control to stop (<see cref="RunDeadlineHandler"/>), so it never loads or writes the saga. When it fires for a
/// run that already finished, its control is gone and the message is a harmless no-op; the saga was already deleted by the
/// terminal finaliser (<see cref="RunFinalization"/>), so nothing else is needed.</summary>
/// <param name="RunId">The run to stop if it is still running at its deadline.</param>
public sealed record RunDeadline(Guid RunId);

/// <summary>The durable-queue handler for a run's wall-clock deadline (§8.4): asks the still-running run's in-process control to
/// stop (a terminal <c>run_deadline_exceeded</c> failure the executor then finalises). A plain handler, not a saga handler — it
/// touches only the in-process <see cref="IRunControlRegistry"/>, never the saga document, so it can never race the terminal
/// finaliser's saga delete. If the run already finished, its control is gone and this is a no-op (the deadline is spent
/// harmlessly). First-writer-wins in <see cref="RunControl"/> means a late deadline never overrides a user cancel in flight.</summary>
public static class RunDeadlineHandler
{
    /// <summary>Stops the run if it is still being driven at its deadline; a no-op once it has finished.</summary>
    /// <param name="command">The elapsed deadline (its run id).</param>
    /// <param name="controls">The in-process run-control registry.</param>
    public static void Handle(RunDeadline command, IRunControlRegistry controls)
    {
        if (controls.TryGet(command.RunId, out var control))
        {
            control.Stop(RunStopReason.Deadline);
        }
    }
}

/// <summary>
/// The run executor saga (§11/§14.2), the net-new durable-orchestration piece. Marten-backed saga storage (automatic via
/// the host's <c>IntegrateWithWolverine()</c>) holds the immutable run definition; the mutable execution state — checkpoint,
/// status, result — lives in the executor-owned <see cref="RunProgress"/> document so the long-running executor's own-session
/// writes never contend with Wolverine's saga persistence. <see cref="StartOrHandle"/> pins the run and kicks the durable
/// <see cref="ExecuteRun"/> plus the delayed wall-clock <see cref="RunDeadline"/>. The saga is <b>completed by deletion in the
/// same transaction as the run's terminal disposition</b> (<see cref="RunFinalization.Apply"/> does <c>session.Delete</c>), so a
/// finished run's <c>script</c>+<c>inputs</c> never linger and there is no separate cleanup step that a crash could lose
/// (SECURITY.md "Durable state at rest"). Because messaging is durable, the orchestration survives process restarts and the run
/// resumes from its last checkpoint (its saga survives precisely because a non-finalised run is never deleted).
/// </summary>
public sealed class RunExecutorSaga : Saga
{
    /// <summary>The run/saga id (the Marten document id).</summary>
    public Guid Id { get; set; }

    /// <summary>The payload's logical name (observability).</summary>
    public string PayloadName { get; set; } = "";

    /// <summary>The executed script's hash (drift/audit). Non-empty once started — the sentinel <see cref="StartOrHandle"/>
    /// reads to tell a fresh saga (just constructed by Wolverine) from an already-started one loaded under a redelivery.</summary>
    public string ScriptHash { get; set; } = "";

    /// <summary>The pinned managed payload, or null for an inline run.</summary>
    public Guid? PayloadId { get; set; }

    /// <summary>The pinned revision, or null for an inline run.</summary>
    public int? PayloadRevision { get; set; }

    /// <summary>The payload document JSON (scrubbed, executable) — re-run source for a resumed run.</summary>
    public string Script { get; set; } = "";

    /// <summary>The run inputs JSON — re-connect source for a resumed run (credentials are by-reference only).</summary>
    public string Inputs { get; set; } = "";

    /// <summary>Starts the saga — <b>idempotently</b>. Named <c>StartOrHandle</c> (not <c>Start</c>) so Wolverine generates the
    /// load-first saga path: it pulls the run id from the <see cref="SagaIdentity"/> on <see cref="StartRun.RunId"/>, loads any
    /// existing saga, and only inserts when none exists. A first delivery pins the definition and cascades the durable
    /// <see cref="ExecuteRun"/> plus the delayed wall-clock <see cref="RunDeadline"/>; a <b>redelivered or duplicate</b>
    /// <see cref="StartRun"/> for a run whose saga is still present loads that saga (its <see cref="ScriptHash"/> is set) and
    /// returns no messages — a genuine no-op, never a second saga, a re-kicked executor, a second deadline, or the duplicate-key
    /// <c>DocumentAlreadyExistsException</c> a straight <c>Insert</c> would throw. This closes the latent saga-starter race a
    /// redelivered <c>StartRun</c>/<c>PromoteQueued</c> load could otherwise trip (Wolverine's at-least-once delivery); the
    /// outbox makes saga-insert and inbox-ack atomic, so a redelivery only occurs for a run whose original insert rolled back
    /// (its saga is genuinely absent) and never for one that already finished.</summary>
    /// <param name="command">The resolved run definition.</param>
    /// <returns>The cascading messages on a fresh start, or none on an idempotent redelivery.</returns>
    public OutgoingMessages StartOrHandle(StartRun command)
    {
        if (ScriptHash.Length != 0)
        {
            return []; // already started (loaded under a redelivery) — no second saga, no duplicate cascade
        }

        // Pin the saga's document id on the fresh-start branch: Wolverine's maybe-existing codegen sets the id only on the
        // MessageContext (for cascade correlation), not on the new saga instance, so the starting method must set it or Marten
        // assigns a random guid and the executor's ExecuteRun can never load the saga by run id.
        Id = command.RunId;
        PayloadName = command.PayloadName;
        ScriptHash = command.ScriptHash;
        PayloadId = command.PayloadId;
        PayloadRevision = command.PayloadRevision;
        Script = command.Script;
        Inputs = command.Inputs;

        // Kick the executor now and schedule the wall-clock deadline as a plain delayed message (not a saga timeout) — the
        // deadline never needs the saga, only the run's in-process control (RunDeadlineHandler), so it can never race the
        // finaliser's saga delete.
        var outgoing = new OutgoingMessages { new ExecuteRun(command.RunId) };
        outgoing.Delay(new RunDeadline(command.RunId), TimeSpan.FromMilliseconds(command.DeadlineMs));
        return outgoing;
    }
}
