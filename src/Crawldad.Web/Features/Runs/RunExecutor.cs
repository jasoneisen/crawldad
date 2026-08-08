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
    /// <summary>Drives one run (or resumes it) to a terminal state, under the message's tenant (CD-1).</summary>
    /// <param name="command">The run to execute.</param>
    /// <param name="executor">The run executor.</param>
    /// <param name="envelope">The message envelope — its tenant id scopes every session the executor opens for this run.</param>
    /// <param name="ct">The handler cancellation token (cancelled on host shutdown).</param>
    public static Task Handle(ExecuteRun command, RunExecutor executor, Envelope envelope, CancellationToken ct) =>
        executor.ExecuteAsync(command.RunId, envelope.TenantId, ct);
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
    public async Task ExecuteAsync(Guid runId, string? tenantId, CancellationToken handlerCt)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return; // a run without a tenant cannot be resolved — fail closed (never touch the default partition)
        }

        var loaded = await LoadRunnableAsync(runId, tenantId, handlerCt);
        if (loaded is null)
        {
            return; // unknown run, already terminal (idempotent redelivery), or not yet set up
        }

        var (saga, progress) = loaded.Value;

        // Claim the run so a redelivered/recovered ExecuteRun for a run already in flight in this process is a no-op (the
        // startup recovery scan and a durable redelivery could both target the same run) — one executor drives it.
        var control = controls.GetOrAdd(runId);
        if (!control.TryClaim())
        {
            return;
        }

        try
        {
            await DriveAsync(runId, tenantId, saga, progress, control, handlerCt);
        }
        finally
        {
            controls.Remove(runId);
            signals.Remove(runId); // no more events for this run — drop its SSE notification slot
        }
    }

    private async Task DriveAsync(Guid runId, string tenantId, RunExecutorSaga saga, RunProgress progress, RunControl control, CancellationToken handlerCt)
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
            }

            // Otherwise host shutdown interrupted the run: leave RunProgress "running" (do NOT finalise) and return
            // normally so the message is acked cleanly. The startup recovery scan on the next host re-publishes
            // ExecuteRun and the executor resumes from the last durable checkpoint (§11).
            return;
        }

        await FinalizeAsync(runId, tenantId, outcome, control, runCt);
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

    // Maps the interpreter outcome to the persisted disposition: append the scrubbed trace + terminal event and stamp the
    // executor-owned RunProgress read model. A cooperative stop is a user cancel (cancelled + partial) unless the deadline
    // fired (a terminal run_deadline_exceeded failure, §8.4).
    private async Task FinalizeAsync(Guid runId, string tenantId, RunOutcome outcome, RunControl control, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);

        // The interpreter's trace events were already appended live through the observer (§13, in occurrence order), so
        // outcome.Events is empty on this path — nothing to replay here; only the terminal event + read model remain.
        var progress = (await session.LoadAsync<RunProgress>(runId, ct))!;
        progress.Stats = outcome.Stats;

        switch (outcome.Status)
        {
            case RunStatus.Succeeded:
                progress.Status = RunStatus.Succeeded;
                progress.ResultJson = scrubber.ScrubJson(outcome.Result)!.Value.GetRawText(); // Result is non-null on success
                session.Events.Append(runId, new RunSucceeded(outcome.Stats, clock.GetUtcNow()));
                break;

            case RunStatus.Failed:
                var failure = RunEventScrubber.ScrubFailure(outcome.Failure!, scrubber);
                progress.Status = RunStatus.Failed;
                progress.Failure = failure;
                session.Events.Append(runId, new RunFailed(failure, outcome.Stats, clock.GetUtcNow()));
                break;

            case RunStatus.Cancelled when control.StopReason == RunStopReason.Deadline:
                var deadline = new RunFailureDetail("terminal", DeadlineExceededCode, "the run exceeded its wall-clock deadline (§8.4)", new RunStepRef(0, "run"));
                progress.Status = RunStatus.Failed;
                progress.Failure = deadline;
                session.Events.Append(runId, new RunFailed(deadline, outcome.Stats, clock.GetUtcNow()));
                break;

            default: // RunStatus.Cancelled — a cooperative user cancel
                progress.Status = RunStatus.Cancelled;
                progress.PartialJson = scrubber.ScrubJson(outcome.Partial)?.GetRawText();
                session.Events.Append(runId, new RunCancelled(outcome.Stats, clock.GetUtcNow()));
                break;
        }

        session.Store(progress);
        await session.SaveChangesAsync(ct);
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
/// The startup recovery for interrupted runs (§11): when a host boots on a Marten schema whose durable queues survived a
/// prior host, any run still marked <see cref="RunStatus.Running"/> in <see cref="RunProgress"/> was interrupted mid-flight
/// (the prior host was killed after a checkpoint but before finalising). This hosted service re-publishes a durable
/// <see cref="ExecuteRun"/> for each, and the executor resumes it from its last checkpoint with a fresh browser session —
/// not from step 0. It runs after the Wolverine host services (registered later), so the durable local queue is live.
/// </summary>
/// <param name="store">The Marten store used to scan for interrupted runs.</param>
/// <param name="scopeFactory">Creates a scope to resolve the (scoped) message bus for re-publishing.</param>
/// <param name="tenants">The configured tenant directory — the fan-out set the per-tenant scan iterates (CD-1).</param>
public sealed class RunRecoveryService(IDocumentStore store, IServiceScopeFactory scopeFactory, TenantRegistry tenants) : IHostedService
{
    /// <summary>Scans for interrupted runs and re-publishes their executor messages, <b>per tenant</b>. Under conjoined
    /// tenancy (CD-1) every query is tenant-scoped, so recovery fans out over each configured tenant and re-publishes each
    /// interrupted run's <see cref="ExecuteRun"/> tagged with that tenant — so the executor resumes it under the same
    /// partition it started in. On a fresh database each per-tenant query simply finds none.</summary>
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
        }
    }

    /// <summary>No teardown work.</summary>
    /// <param name="cancellationToken">Unused.</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
