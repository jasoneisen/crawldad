using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Crawldad.Api.Features.Runs;

/// <summary>Starts the durable executor saga: carries the resolved, scrubbed run definition so the background executor —
/// and any resume after a restart — can re-establish the run without the originating HTTP request. The run id is the
/// <see cref="SagaIdentity"/>, so a redelivered or duplicate message correlates to the existing saga and is a no-op (see <see cref="RunExecutorSaga.StartOrHandle"/>).</summary>
public sealed record StartRun(
    [property: SagaIdentity] Guid RunId,
    string PayloadName,
    string ScriptHash,
    string Script,
    string Inputs,
    Guid? PayloadId,
    int? PayloadRevision,
    int DeadlineMs);

/// <summary>The durable local-queue message that drives one run to a terminal state. Handled by the executor (which owns
/// its own Marten sessions); a process death before it is acked leaves it in the durable inbox to be redelivered on
/// restart, resuming the run from its last checkpoint.</summary>
public sealed record ExecuteRun(Guid RunId);

/// <summary>The run's wall-clock deadline: a durable <b>scheduled</b> message, deliberately <b>not</b> a saga timeout —
/// its only job is asking the still-running run's in-process control to stop (<see cref="RunDeadlineHandler"/>), so it
/// never loads or writes the saga. A harmless no-op once the run has already finished.</summary>
public sealed record RunDeadline(Guid RunId);

/// <summary>The durable-queue handler for a run's wall-clock deadline: asks the still-running run's in-process control to
/// stop. A plain handler, not a saga handler — it touches only <see cref="IRunControlRegistry"/>, never the saga document,
/// so it can never race the terminal finaliser's saga delete. First-writer-wins means a late deadline never overrides an in-flight user cancel.</summary>
public static class RunDeadlineHandler
{
    /// <summary>Stops the run if it is still being driven at its deadline; a no-op once it has finished.</summary>
    public static void Handle(RunDeadline command, IRunControlRegistry controls)
    {
        if (controls.TryGet(command.RunId, out var control))
        {
            control.Stop(RunStopReason.Deadline);
        }
    }
}

/// <summary>The run executor saga: Marten-backed storage holds the immutable run definition; the mutable execution state
/// (checkpoint, status, result) lives in <see cref="RunProgress"/> instead, so the executor's writes never contend with
/// saga persistence. Completed by <b>deletion in the same transaction</b> as the run's terminal disposition, so nothing lingers.</summary>
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

    /// <summary>Starts the saga <b>idempotently</b>. Named <c>StartOrHandle</c> so Wolverine generates the load-first saga
    /// path (load any existing saga by <see cref="SagaIdentity"/>, insert only when none exists): a first delivery pins
    /// the definition and cascades <see cref="ExecuteRun"/> + the delayed <see cref="RunDeadline"/>; a redelivered or duplicate <see cref="StartRun"/> is a genuine no-op, never a second saga or duplicate-key error.</summary>
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
