using Crawldad.Contracts.Runs;
using Marten;
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
/// <param name="DeadlineMs">The run wall-clock cap in milliseconds (§8.4), scheduled as the saga timeout.</param>
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

/// <summary>The run's wall-clock deadline (§8.4) as a Wolverine saga timeout: scheduled by <see cref="RunExecutorSaga.StartOrHandle"/>
/// and, when it elapses, routed back to the saga (auto-ignored if the saga is already gone). Marked with the saga id so it
/// correlates to its run. Wolverine scheduled messages are never cancelled when a run finishes early, so the same timeout
/// doubles as the saga's janitor (see <see cref="RunExecutorSaga.Handle(RunDeadline, IDocumentSession, IRunControlRegistry, CancellationToken)"/>).</summary>
/// <param name="RunId">The run/saga id.</param>
/// <param name="DeadlineDelay">How long after start the deadline fires.</param>
public sealed record RunDeadline([property: SagaIdentity] Guid RunId, TimeSpan DeadlineDelay) : TimeoutMessage(DeadlineDelay);

/// <summary>The prompt "the run reached a terminal state" signal (§14.2, CRAWLDAD_DESIGN.md §14.2): published by the shared
/// terminal finalisers (the executor's <see cref="ExecuteRunHandler"/> and the CD-15 <see cref="SyncRunSupervisor"/>) once a
/// run commits its terminal disposition, so the saga is <see cref="Saga.MarkCompleted"/>-ed at once — bounding the at-rest
/// retention of the run's <c>script</c>+<c>inputs</c> in <c>mt_doc_runexecutorsaga</c> to the run's own duration rather than
/// letting it linger. Marked with the saga id. Idempotent: a redelivery (or arrival after the deadline janitor already
/// reclaimed the saga) hits <see cref="RunExecutorSaga.NotFound(RunFinished)"/> and no-ops.</summary>
/// <param name="RunId">The finished run/saga id.</param>
public sealed record RunFinished([property: SagaIdentity] Guid RunId);

/// <summary>
/// The run executor saga (§11/§14.2), the net-new durable-orchestration piece. Marten-backed saga storage (automatic via
/// the host's <c>IntegrateWithWolverine()</c>) holds the immutable run definition; the mutable execution state — checkpoint,
/// status, result — lives in the executor-owned <see cref="RunProgress"/> document so the long-running executor's own-session
/// writes never contend with Wolverine's saga persistence. <see cref="StartOrHandle"/> pins the run and kicks the durable
/// <see cref="ExecuteRun"/> plus the wall-clock <see cref="RunDeadline"/>; the run's terminal completion reclaims the saga two
/// ways so its <c>script</c>+<c>inputs</c> never linger indefinitely (SECURITY.md "Durable state at rest"): promptly on the
/// finaliser's <see cref="RunFinished"/> (<see cref="Handle(RunFinished)"/>), and — as the crash-safe backstop — on the
/// already-scheduled <see cref="RunDeadline"/> (<see cref="Handle(RunDeadline, IDocumentSession, IRunControlRegistry, CancellationToken)"/>),
/// which also enforces the deadline itself. Because messaging is durable, the orchestration survives process restarts and the
/// run resumes from its last checkpoint.
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
    /// <see cref="ExecuteRun"/> plus the wall-clock <see cref="RunDeadline"/>; a <b>redelivered or duplicate</b> <see cref="StartRun"/>
    /// for the same run loads the already-started saga (its <see cref="ScriptHash"/> is set) and returns no messages — a genuine
    /// no-op, never a second saga, a re-kicked executor, a second deadline timer, or the duplicate-key
    /// <c>DocumentAlreadyExistsException</c> a straight <c>Insert</c> would throw. This closes the latent saga-starter race a
    /// redelivered <c>StartRun</c>/<c>PromoteQueued</c> load could otherwise trip (Wolverine's at-least-once delivery).</summary>
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

        return new OutgoingMessages
        {
            new ExecuteRun(command.RunId),
            new RunDeadline(command.RunId, TimeSpan.FromMilliseconds(command.DeadlineMs)),
        };
    }

    /// <summary>Enforces the wall-clock deadline (§8.4) <b>and</b> doubles as the saga's crash-safe janitor (§14.2). The
    /// already-scheduled deadline always fires — Wolverine never cancels a scheduled message when a run finishes early — so it
    /// checks the run's authoritative disposition in <see cref="RunProgress"/>:
    /// <list type="bullet">
    /// <item>the run is already terminal (or its progress is gone): its work is done, so <see cref="Saga.MarkCompleted"/> the
    /// saga — bounding the at-rest retention of its <c>script</c>+<c>inputs</c> to <c>deadlineMs</c> with zero new messages. This
    /// is the backstop for a crash between the terminal commit and the finaliser's <see cref="RunFinished"/>: the durable timeout
    /// reclaims the saga after the restart.</item>
    /// <item>the run is still being driven: it has reached its wall-clock deadline, so ask its control to stop (a terminal
    /// <c>run_deadline_exceeded</c> failure, §8.4). The saga is <b>not</b> completed here — its <c>script</c>+<c>inputs</c> are
    /// still the resume source if the run is mid-recovery (the resume invariant); the executor then publishes
    /// <see cref="RunFinished"/>, which completes the saga.</item>
    /// </list></summary>
    /// <param name="_">The elapsed deadline (routing only).</param>
    /// <param name="session">The message's tenant-scoped Marten session — reads the authoritative <see cref="RunProgress"/>.</param>
    /// <param name="controls">The in-process run-control registry.</param>
    /// <param name="ct">Cancels the progress read.</param>
    public async Task Handle(RunDeadline _, IDocumentSession session, IRunControlRegistry controls, CancellationToken ct)
    {
        var progress = await session.LoadAsync<RunProgress>(Id, ct);
        if (progress is null || progress.Status != RunStatus.Running)
        {
            MarkCompleted();
            return;
        }

        if (controls.TryGet(Id, out var control))
        {
            control.Stop(RunStopReason.Deadline);
        }
    }

    /// <summary>Prompt cleanup (§14.2): the shared terminal finaliser signals the run reached a terminal state, so complete the
    /// saga now — its <c>script</c>+<c>inputs</c> are no longer a resume source. This covers every saga-bearing terminal path,
    /// including a run the deadline itself stopped (whose already-spent timeout can no longer act as the janitor).</summary>
    /// <param name="_">The finished-run signal (routing only; the run is this saga).</param>
    public void Handle(RunFinished _) => MarkCompleted();

    /// <summary>The idempotent no-op for a <see cref="RunFinished"/> whose saga is already gone — the deadline janitor won the
    /// race, or this is a redelivery. Without it Wolverine would throw <c>UnknownSagaException</c> for the missing saga.</summary>
    /// <param name="_">The finished-run signal (routing only).</param>
    public static void NotFound(RunFinished _)
    {
    }
}
