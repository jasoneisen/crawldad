using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Crawldad.Web.Features.Runs;

/// <summary>The durable local-queue handler for <see cref="ExecuteRun"/> (§11): a thin shell over
/// <see cref="RunExecutor"/>. It injects <see cref="IDocumentStore"/> (not a request session) so no per-request transaction
/// wraps the long-running executor, which owns its own Marten sessions. A host-shutdown interruption returns cleanly and
/// leaves the run resumable — <see cref="RunRecoveryService"/> re-publishes it on the next host.</summary>
public static class ExecuteRunHandler
{
    /// <summary>Drives one run (or resumes it) to a terminal state, under the message's tenant (CD-1). When the run reaches a
    /// terminal state (freeing its slot), it triggers promotion of the tenant's oldest queued run (CD-16) — a durable, no-op-if-
    /// nothing-queued trigger; a run merely interrupted for recovery frees no slot and triggers nothing.</summary>
    /// <param name="command">The run to execute.</param>
    /// <param name="executor">The run executor.</param>
    /// <param name="bus">The bus the queue promotion trigger is published on.</param>
    /// <param name="envelope">The message envelope — its tenant id scopes every session the executor opens for this run.</param>
    /// <param name="ct">The handler cancellation token (cancelled on host shutdown).</param>
    public static async Task Handle(ExecuteRun command, RunExecutor executor, IMessageBus bus, Envelope envelope, CancellationToken ct)
    {
        if (await executor.ExecuteAsync(command.RunId, envelope.TenantId, ct))
        {
            await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = envelope.TenantId });
        }
    }
}

/// <summary>
/// The long-running run executor (§11/§14.2), the deliberate departure from one-transaction-per-request: it <b>owns its own
/// Marten sessions</b>, opening one per checkpoint so progress commits durably mid-run and a killed run resumes from its last
/// checkpoint. It opens the per-run secret scope for the whole execution (§12) — a fresh <c>ConnectAsync</c> re-registers the
/// resolved credential on resume — and drives the interpreter with an observer that persists checkpoints and surfaces the
/// cooperative cancel/deadline. It maps the interpreter outcome to the terminal disposition, scrubbing the result/failure and
/// appending the terminal trace event, and writes everything a poller reads into the executor-owned <see cref="RunProgress"/>.
/// </summary>
/// <param name="store">The Marten store (the executor opens its own sessions from it).</param>
/// <param name="registry">Resolves the backend adapter named by the payload.</param>
/// <param name="sinks">Resolves download sinks named by the payload.</param>
/// <param name="scrubber">Scrubs credentials from every persisted event, checkpoint, and stored result (§12).</param>
/// <param name="secretScope">The per-run secret registry opened for the whole execution (§12).</param>
/// <param name="controls">The in-process stop-signal registry (cancel/deadline).</param>
/// <param name="admissionGate">The concurrent-run admission gate (CD-3): the executor occupies the run's slot while it
/// drives it (self-healing the count after a restart re-runs an already-admitted run) and frees it at finalisation.</param>
/// <param name="screenshots">The failure-screenshot blob store the interpreter captures into (§13).</param>
/// <param name="signals">The in-process SSE notification hub pinged after each durable append (§11/§13).</param>
/// <param name="lifetime">The host lifetime, so an in-flight run observes shutdown and leaves itself resumable.</param>
/// <param name="runLimits">The server-side resource-limit options (CD-3/§12): the executor derives the interpreter's
/// mid-run caps from them once. A payload can never raise them.</param>
/// <param name="clock">The time seam for trace timestamps.</param>
public sealed class RunExecutor(
    IDocumentStore store,
    IBrowserBackendRegistry registry,
    IDownloadSinkRegistry sinks,
    CredentialScrubber scrubber,
    IRunSecretScope secretScope,
    IRunControlRegistry controls,
    IRunAdmissionGate admissionGate,
    IScreenshotStore screenshots,
    RunEventSignals signals,
    IHostApplicationLifetime lifetime,
    IOptions<RunLimitsOptions> runLimits,
    TimeProvider clock)
{
    /// <summary>The terminal failure code for a run that outran its wall-clock deadline (§8.4).</summary>
    public const string DeadlineExceededCode = "run_deadline_exceeded";

    // The interpreter's mid-run resource caps (CD-3/§12), resolved once from the bound options for every run this executor drives.
    private readonly RunLimits _limits = runLimits.Value.ToRunLimits();

    /// <summary>Executes (or resumes) the run to a terminal state under <paramref name="tenantId"/>. A host-shutdown
    /// interruption is left un-finalised so the durable <see cref="ExecuteRun"/> message is redelivered and the run resumes
    /// on restart. Every Marten session the executor opens is scoped to the run's tenant, so its trace appends and progress
    /// writes land in the same tenant partition the run started under (CD-1).</summary>
    /// <param name="runId">The run to execute.</param>
    /// <param name="tenantId">The run's tenant (from the message envelope); a run with no tenant is a fail-closed no-op.</param>
    /// <param name="handlerCt">The handler cancellation token (cancelled on host shutdown).</param>
    /// <returns>True when the run reached a terminal state and freed its slot for good (so the caller promotes the tenant's
    /// oldest queued run, CD-16); false when nothing ran or the run was merely interrupted for recovery (its slot is not free).</returns>
    public async Task<bool> ExecuteAsync(Guid runId, string? tenantId, CancellationToken handlerCt)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return false; // a run without a tenant cannot be resolved — fail closed (never touch the default partition)
        }

        var loaded = await LoadRunnableAsync(runId, tenantId, handlerCt);
        if (loaded is null)
        {
            return false; // unknown run, already terminal (idempotent redelivery), or not yet set up
        }

        var (saga, progress) = loaded.Value;

        // Claim the run so a redelivered/recovered ExecuteRun for a run already in flight in this process is a no-op (the
        // startup recovery scan and a durable redelivery could both target the same run) — one executor drives it.
        var control = controls.GetOrAdd(runId);
        if (!control.TryClaim())
        {
            return false;
        }

        // Occupy the run's admission slot for as long as this executor drives it (CD-3): a no-op for a fresh run the HTTP
        // admission already counted, and the real (re)registration for a run recovered after a restart — so the in-memory
        // slot count self-heals. Freed in the finally when the run reaches terminal (or this host is torn down mid-run).
        admissionGate.Occupy(tenantId, runId);
        try
        {
            return await DriveAsync(runId, tenantId, saga, progress, control, handlerCt);
        }
        finally
        {
            admissionGate.Release(tenantId, runId);
            controls.Remove(runId);
            signals.Remove(runId); // no more events for this run — drop its SSE notification slot
        }
    }

    // Drives the run to a terminal state, returning true when it finalised (freeing its slot for good) and false when host
    // shutdown interrupted it — the latter leaves the run resumable and its slot not truly free (a fresh host re-drives it).
    private async Task<bool> DriveAsync(Guid runId, string tenantId, RunExecutorSaga saga, RunProgress progress, RunControl control, CancellationToken handlerCt)
    {
        // The deadline source (§8.4) forcibly interrupts a run stuck mid-call; it is linked in beside host shutdown so the
        // interpreter's operations observe both. The control binds it so the saga's deadline timeout can fire it.
        using var deadlineCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(handlerCt, lifetime.ApplicationStopping, deadlineCts.Token);
        var runCt = linked.Token;
        control.UseForcibleCancellation(deadlineCts);

        // The per-run secret scope spans the WHOLE execution (§12), including retries; a fresh ConnectAsync inside the
        // interpreter re-registers the resolved credential, so a resumed run re-establishes the scrub set naturally.
        using var runSecrets = secretScope.Begin();

        var resume = await LoadResumeAsync(runId, tenantId, progress, runCt);

        using var payloadDocument = JsonDocument.Parse(saga.Script);
        using var inputsDocument = JsonDocument.Parse(saga.Inputs);
        var input = JsonValues.FromJson(inputsDocument.RootElement) as Dictionary<string, object?>
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);

        var observer = new ExecutorObserver(runId, tenantId, control, store, scrubber, signals, clock);
        RunOutcome outcome;
        try
        {
            outcome = await new RunInterpreter(payloadDocument.RootElement, input, registry, sinks, clock, tenantId, observer, resume, screenshots, _limits).RunAsync(runCt);
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
            if (control.StopReason == RunStopReason.Deadline)
            {
                // The wall-clock deadline forcibly cancelled a stuck run (§8.4): finalise a terminal failure. The
                // interpreter's `await using` already tore the backend session down cleanly.
                await FinalizeAsync(runId, tenantId, DeadlineOutcome(), control, CancellationToken.None);
                return true; // terminal — the slot is free for a queued run (CD-16)
            }

            // Otherwise host shutdown interrupted the run: leave RunProgress "running" (do NOT finalise) and return
            // normally so the message is acked cleanly. The startup recovery scan on the next host re-publishes
            // ExecuteRun and the executor resumes from the last durable checkpoint (§11). The slot is NOT truly free.
            return false;
        }

        await FinalizeAsync(runId, tenantId, outcome, control, runCt);
        return true; // terminal — the slot is free for a queued run (CD-16)
    }

    // A synthetic stopped outcome for a run the deadline forcibly cancelled mid-call (there is no salvageable result);
    // FinalizeAsync maps a Cancelled outcome under a Deadline stop reason to the terminal run_deadline_exceeded failure.
    private static RunOutcome DeadlineOutcome() =>
        new(RunStatus.Cancelled, null, null, null, new RunStats(0, 0, 0, 0, 0), []);

    // Loads the run definition + progress and returns them only when the run is actually runnable: an unknown run (no saga)
    // or one that is no longer running (already terminal — an idempotent redelivery) yields null and the executor no-ops.
    private async Task<(RunExecutorSaga Saga, RunProgress Progress)?> LoadRunnableAsync(Guid runId, string tenantId, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        var saga = await session.LoadAsync<RunExecutorSaga>(runId, ct);
        if (saga is null)
        {
            return null;
        }

        var progress = await session.LoadAsync<RunProgress>(runId, ct);
        return progress is { Status: RunStatus.Running } ? (saga, progress) : null;
    }

    // Restores the resume state from the last durable checkpoint (§11) and records the resume in the trace, or returns null
    // for a fresh run. Appended from the executor's own session so the RunResumed marker is durable independent of finalisation.
    private async ValueTask<ResumeState?> LoadResumeAsync(Guid runId, string tenantId, RunProgress progress, CancellationToken ct)
    {
        if (progress.Checkpoint is not { } stored)
        {
            return null;
        }

        await using var session = store.LightweightSession(tenantId);
        session.Events.Append(runId, new RunResumed(stored.StepIndex, stored.Name, clock.GetUtcNow()));
        await session.SaveChangesAsync(ct);
        signals.Notify(runId); // a tailing SSE client sees the resume marker live

        return new ResumeState(stored.Name, stored.Sequence, stored.StepIndex, ParseJson(stored.CursorJson), ParseJson(stored.VarsJson));
    }

    // Maps the interpreter outcome to the persisted disposition through the shared RunFinalization (§11, shared with the
    // CD-15 sync auto-upgrade supervisor): append the scrubbed trace + terminal event, stamp the executor-owned RunProgress
    // read model, and free the slot BEFORE the terminal status commits (so a poller can immediately start another run, CD-3).
    // outcome.Events is empty on this path — the observer already appended the trace live (§13) — so nothing is replayed. A
    // cooperative stop is a user cancel (cancelled + partial) unless the deadline fired (a terminal run_deadline_exceeded, §8.4).
    private async Task FinalizeAsync(Guid runId, string tenantId, RunOutcome outcome, RunControl control, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        var progress = (await session.LoadAsync<RunProgress>(runId, ct))!;
        RunFinalization.Apply(session, runId, tenantId, outcome, control.StopReason, progress, scrubber, admissionGate, clock);
        await session.SaveChangesAsync(ct); // the executor's outer finally repeats the slot release idempotently on non-finalised paths
        signals.Notify(runId); // the terminal event closes any live SSE tail
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // The executor's run observer (§11/§13): appends the interpreter's live trace events and each reached checkpoint from the
    // executor's OWN session, scrubbed at the RunEventScrubber chokepoint (§12) and committed immediately — so a tailing SSE
    // client sees them at once and a kill after a checkpoint resumes there. Every append pings the SSE notification hub.
    private sealed class ExecutorObserver(Guid runId, string tenantId, RunControl control, IDocumentStore store, CredentialScrubber scrubber, RunEventSignals signals, TimeProvider clock) : IRunObserver
    {
        public bool CancellationRequested => control.StopRequested;

        public async ValueTask EmitAsync(object traceEvent, CancellationToken ct)
        {
            await using var session = store.LightweightSession(tenantId);
            session.Events.Append(runId, RunEventScrubber.Scrub(traceEvent, scrubber));
            await session.SaveChangesAsync(ct);
            signals.Notify(runId);
        }

        public async ValueTask CheckpointReachedAsync(CheckpointSnapshot checkpoint, CancellationToken ct)
        {
            await using var session = store.LightweightSession(tenantId);
            session.Events.Append(runId, new RunCheckpointReached(checkpoint.Name, checkpoint.Sequence, clock.GetUtcNow()));

            var progress = (await session.LoadAsync<RunProgress>(runId, ct))!;
            progress.Checkpoint = new StoredCheckpoint(
                checkpoint.Name,
                checkpoint.Sequence,
                checkpoint.StepIndex,
                scrubber.Scrub(checkpoint.Cursor.GetRawText()),
                scrubber.Scrub(checkpoint.Vars.GetRawText()));
            session.Store(progress);

            await session.SaveChangesAsync(ct);
            signals.Notify(runId);
        }
    }
}

/// <summary>
/// The startup recovery for interrupted <b>and queued</b> runs (§11/CD-16): when a host boots on a Marten schema whose durable
/// queues survived a prior host, any run still marked <see cref="RunStatus.Running"/> in <see cref="RunProgress"/> was
/// interrupted mid-flight, and any surviving <see cref="QueuedRun"/> was waiting at the cap when the prior host died. This
/// hosted service re-publishes a durable <see cref="ExecuteRun"/> for each interrupted run (the executor resumes it from its
/// last checkpoint with a fresh browser session — not from step 0) and re-triggers <see cref="PromoteQueued"/> for every tenant
/// with a non-empty queue so queued runs start (in FIFO order) once slots are free. FIFO ordering across the restart is kept by
/// the queue's per-tenant sequence, which self-seeds above the surviving high-water mark on its first post-restart use (so no
/// startup-ordering seed is needed here). It runs after the Wolverine host services (registered later), so the durable local
/// queue is live.
/// </summary>
/// <param name="store">The Marten store used to scan for interrupted and queued runs.</param>
/// <param name="scopeFactory">Creates a scope to resolve the (scoped) message bus for re-publishing.</param>
/// <param name="tenants">The configured tenant directory — the fan-out set the per-tenant scan iterates (CD-1).</param>
public sealed class RunRecoveryService(IDocumentStore store, IServiceScopeFactory scopeFactory, TenantRegistry tenants) : IHostedService
{
    /// <summary>Scans for interrupted and queued runs and re-drives them, <b>per tenant</b>. Under conjoined tenancy (CD-1)
    /// every query is tenant-scoped, so recovery fans out over each configured tenant: it re-publishes each interrupted run's
    /// <see cref="ExecuteRun"/> tagged with that tenant, and (for a tenant with queued runs) publishes a <see cref="PromoteQueued"/>
    /// so its queue drains into free slots in FIFO order. On a fresh database each per-tenant query finds none.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        foreach (var tenantId in tenants.TenantIds)
        {
            await using var session = store.QuerySession(tenantId);
            var interrupted = await session.Query<RunProgress>()
                .Where(progress => progress.Status == RunStatus.Running)
                .ToListAsync(cancellationToken);

            foreach (var run in interrupted)
            {
                await bus.PublishAsync(new ExecuteRun(run.Id), new DeliveryOptions { TenantId = tenantId });
            }

            // Surviving queued runs (CD-16): re-trigger promotion so the queue drains once slots free (the trigger drains one
            // and re-triggers for each further free slot, in sequence order).
            if (await session.Query<QueuedRun>().AnyAsync(cancellationToken))
            {
                await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
            }
        }
    }

    /// <summary>No teardown work.</summary>
    /// <param name="cancellationToken">Unused.</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
