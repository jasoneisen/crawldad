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

/// <summary>The durable local-queue handler for <see cref="ExecuteRun"/>: a thin shell over <see cref="RunExecutor"/>. It
/// injects <see cref="IDocumentStore"/> (not a request session) so no per-request transaction wraps the long-running
/// executor. A host-shutdown interruption returns cleanly and leaves the run resumable via <see cref="RunRecoveryService"/>.</summary>
public static class ExecuteRunHandler
{
    /// <summary>Drives one run (or resumes it) to a terminal state. When the run reaches terminal (freeing its slot and
    /// deleting its saga), it triggers promotion of the tenant's oldest queued run; a run merely interrupted for
    /// recovery frees no slot and triggers nothing.</summary>
    public static async Task Handle(ExecuteRun command, RunExecutor executor, IMessageBus bus, Envelope envelope, CancellationToken ct)
    {
        if (await executor.ExecuteAsync(command.RunId, envelope.TenantId, ct))
        {
            await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = envelope.TenantId });

            // The run reached a durable terminal disposition — notify downstream subscribers (webhook fan-out) off the
            // execution path. Post-commit like PromoteQueued, so at-least-once; the subscriber derives everything from the
            // committed run state, so a duplicate is harmless.
            await bus.PublishAsync(new RunFinalized(command.RunId), new DeliveryOptions { TenantId = envelope.TenantId });
        }
    }
}

/// <summary>The long-running run executor: the deliberate departure from one-transaction-per-request. It owns its own
/// Marten sessions, opening one per checkpoint so progress commits durably mid-run and a killed run resumes from its
/// last checkpoint, then maps the interpreter outcome to the terminal disposition.</summary>
public sealed class RunExecutor(
    IDocumentStore store,
    IBrowserBackendRegistry registry,
    IDownloadSinkRegistry sinks,
    CredentialScrubber scrubber,
    IRunSecretScope secretScope,
    ISecretStoreRegistry secretStores,
    IRunControlRegistry controls,
    IRunAdmissionGate admissionGate,
    IScreenshotStore screenshots,
    RunEventSignals signals,
    IHostApplicationLifetime lifetime,
    IOptions<RunLimitsOptions> runLimits,
    TimeProvider clock)
{
    /// <summary>The terminal failure code for a run that outran its wall-clock deadline.</summary>
    public const string DeadlineExceededCode = "run_deadline_exceeded";

    // The interpreter's mid-run resource caps, resolved once from the bound options for every run this executor drives.
    private readonly RunLimits _limits = runLimits.Value.ToRunLimits();

    /// <summary>Executes (or resumes) the run to a terminal state under <paramref name="tenantId"/>. A host-shutdown
    /// interruption is left un-finalised so the durable <see cref="ExecuteRun"/> message is redelivered and the run
    /// resumes on restart. Every session the executor opens is scoped to the run's tenant.</summary>
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

        // Occupy the run's admission slot for as long as this executor drives it: a no-op for a fresh run the HTTP
        // admission already counted, and the real (re)registration for a run recovered after a restart — so the
        // in-memory slot count self-heals. Freed in the finally when the run reaches terminal or is torn down mid-run.
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
        // The deadline source forcibly interrupts a run stuck mid-call; it is linked in beside host shutdown so the
        // interpreter's operations observe both. The control binds it so the saga's deadline timeout can fire it.
        using var deadlineCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(handlerCt, lifetime.ApplicationStopping, deadlineCts.Token);
        var runCt = linked.Token;
        control.UseForcibleCancellation(deadlineCts);

        // The per-run secret scope spans the WHOLE execution, including retries; a fresh ConnectAsync inside the
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
            outcome = await new RunInterpreter(payloadDocument.RootElement, input, registry, sinks, clock, tenantId, observer, resume, screenshots, _limits, secretStores, secretScope).RunAsync(runCt);
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
            if (control.StopReason == RunStopReason.Deadline)
            {
                // The wall-clock deadline forcibly cancelled a stuck run: finalise a terminal failure. The
                // interpreter's `await using` already tore the backend session down cleanly.
                await FinalizeAsync(runId, tenantId, DeadlineOutcome(), control, CancellationToken.None);
                return true; // terminal — the slot is free for a queued run
            }

            // Otherwise host shutdown interrupted the run: leave RunProgress "running" (do NOT finalise) and return
            // normally so the message is acked cleanly. The startup recovery scan on the next host re-publishes
            // ExecuteRun and the executor resumes from the last durable checkpoint. The slot is NOT truly free.
            return false;
        }

        await FinalizeAsync(runId, tenantId, outcome, control, runCt);
        return true; // terminal — the slot is free for a queued run
    }

    // A synthetic stopped outcome for a run the deadline forcibly cancelled mid-call (there is no salvageable result);
    // FinalizeAsync maps a Cancelled outcome under a Deadline stop reason to the terminal run_deadline_exceeded failure.
    private static RunOutcome DeadlineOutcome() =>
        new(RunStatus.Cancelled, null, null, null, new RunStats(0, 0, 0, 0, 0, 0), []);

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

    // Restores the resume state from the last durable checkpoint and records the resume in the trace, or returns null
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

    // Maps the interpreter outcome to the persisted disposition via the shared RunFinalization (also used by the sync
    // auto-upgrade supervisor): appends the trace + terminal event, stamps RunProgress, and frees the slot BEFORE the
    // terminal status commits (so a poller can start another run at once). outcome.Events is empty here — the observer already appended the trace live.
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

    // The executor's run observer: appends the interpreter's live trace events and each reached checkpoint from the
    // executor's OWN session, scrubbed at the RunEventScrubber chokepoint and committed immediately — so a tailing SSE
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

            // The durable cursor + var snapshot is the run's OWN accumulated extracted state: a resumed run restores it and
            // shapes it into the result, so it is scrubbed through ScrubJson — the SAME result-channel posture (exact-secret
            // redaction, but NOT the credential-param rule) RunFinalization stores the result with, stored here as its raw
            // text. The full `Scrub` would rewrite a `token=`-shaped value in extracted content (or a cursor URL) to
            // `[redacted]` on the checkpoint, then restore it corrupted into the result — and break the resume re-navigation
            // of a redacted cursor URL (issue #82). Exact-secret scrubbing stays unconditional, so no run secret is persisted.
            var progress = (await session.LoadAsync<RunProgress>(runId, ct))!;
            progress.Checkpoint = new StoredCheckpoint(
                checkpoint.Name,
                checkpoint.Sequence,
                checkpoint.StepIndex,
                scrubber.ScrubJson(checkpoint.Cursor)!.Value.GetRawText(),
                scrubber.ScrubJson(checkpoint.Vars)!.Value.GetRawText());
            session.Store(progress);

            await session.SaveChangesAsync(ct);
            signals.Notify(runId);
        }
    }
}

/// <summary>Startup recovery for interrupted <b>and</b> queued runs: re-publishes a durable <see cref="ExecuteRun"/> for
/// each run still marked <see cref="RunStatus.Running"/> (the executor resumes from its last checkpoint) and re-triggers
/// <see cref="PromoteQueued"/> for every tenant with a queued run. Runs after the Wolverine host services, so the durable local queue is live.</summary>
public sealed class RunRecoveryService(IDocumentStore store, IServiceScopeFactory scopeFactory, TenantRegistry tenants) : IHostedService
{
    /// <summary>Scans for interrupted and queued runs and re-drives them, <b>per tenant</b>: every query is tenant-scoped,
    /// so recovery fans out over each configured tenant, re-publishing each interrupted run's <see cref="ExecuteRun"/> and
    /// (for a tenant with queued runs) a <see cref="PromoteQueued"/> so its queue drains in FIFO order.</summary>
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

            // Surviving queued runs: re-trigger promotion so the queue drains once slots free (the trigger drains one
            // and re-triggers for each further free slot, in sequence order).
            if (await session.Query<QueuedRun>().AnyAsync(cancellationToken))
            {
                await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
            }
        }
    }

    /// <summary>No teardown work.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
