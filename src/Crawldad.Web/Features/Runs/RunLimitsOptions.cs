using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The server-side resource-limit knobs (CD-3/§12), bound from <c>Crawldad:Limits</c>. These are the caps a deployment
/// tunes; a payload can never raise them (they are not payload fields). The four mid-run caps flow into <see cref="RunLimits"/>
/// for the interpreter; <see cref="MaxConcurrentRunsPerTenant"/> is the billing-critical admission cap (docs/PRODUCT.md
/// §Pv.3) the <see cref="RunAdmissionGate"/> enforces, and it is the global default a per-tenant override
/// (<see cref="Crawldad.Web.Infrastructure.Security.TenantDescriptor.MaxConcurrentRuns"/>) can lower or raise per tier.
/// Defaults are deliberately generous so legitimate runs — and the existing fixtures — never trip them.
/// </summary>
public sealed class RunLimitsOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Crawldad:Limits";

    /// <summary>The global default concurrent-run cap per tenant when a tenant sets no override.</summary>
    public const int DefaultMaxConcurrentRunsPerTenant = 32;

    /// <summary>The global default admission-queue depth per tenant (CD-16) when a tenant sets no override — the number of
    /// at-cap runs that may wait before a further one is rejected <c>429 queue_depth_exceeded</c>. Generous by default so the
    /// queue is effectively unbounded for legitimate use; the per-tier value (Free 10 / Team 100 / Scale 1,000) is set per
    /// tenant (docs/PRODUCT.md §Pv.3).</summary>
    public const int DefaultMaxQueueDepthPerTenant = 1_000;

    /// <summary>The global default max queue wait in milliseconds (CD-16): how long a run may sit queued before it terminates
    /// with <c>queue_wait_exceeded</c>. 0 disables the bound (a queued run waits indefinitely for a slot); the default leaves
    /// it disabled so time-in-queue is capped only where a deployment opts in.</summary>
    public const int DefaultMaxQueueWaitMs = 0;

    /// <summary>The default synchronous-run wall-clock window in milliseconds (CD-15, docs/PRODUCT.md §2.2): how long a
    /// default (non-<c>async</c>) <c>POST /runs</c> may hold the caller's HTTP connection before the run is <b>auto-upgraded
    /// to async</b> — the caller gets <c>202 { runId, status:"running" }</c> and the run keeps executing on the durable
    /// surface. 120 s sits comfortably under every Azure ingress ceiling (Front Door / Container Apps Envoy 240 s, App
    /// Service ~230 s), so a sync request is answered — as a result or an upgrade — before any ingress kills the connection.</summary>
    public const int DefaultSyncUpgradeThresholdMs = 120 * 1000;

    /// <summary>The most semantic steps one execution may run (§12) — <c>max_steps_exceeded</c> beyond it.</summary>
    public int MaxStepsPerRun { get; init; } = RunLimits.DefaultMaxSteps;

    /// <summary>The most bytes one execution may download in total (§9.3/§12) — <c>max_download_bytes_exceeded</c> beyond it.</summary>
    public long MaxDownloadedBytesPerRun { get; init; } = RunLimits.DefaultMaxDownloadedBytes;

    /// <summary>The most trace events one execution may append to its run stream (§12) — <c>max_events_exceeded</c> beyond it.</summary>
    public int MaxEventsPerRun { get; init; } = RunLimits.DefaultMaxEvents;

    /// <summary>The per-evaluation expression fuel budget (§7.2/§12) — <c>expression_budget_exceeded</c> beyond it.</summary>
    public int ExpressionStepBudget { get; init; } = CrawldadExpression.DefaultStepBudget;

    /// <summary>The global default concurrent, non-terminal runs a tenant may have in flight (docs/PRODUCT.md §Pv.3) — a
    /// per-tenant override takes precedence when configured. At the cap, <c>POST /runs</c> queues the run (CD-16, 202
    /// <c>status:"queued"</c>) rather than rejecting it.</summary>
    public int MaxConcurrentRunsPerTenant { get; init; } = DefaultMaxConcurrentRunsPerTenant;

    /// <summary>The global default admission-queue depth per tenant (CD-16) — a per-tenant override
    /// (<see cref="Crawldad.Web.Infrastructure.Security.TenantDescriptor.MaxQueueDepth"/>) takes precedence. At this depth a
    /// further at-cap run is rejected 429 <c>queue_depth_exceeded</c> — the only 429 admission still returns.</summary>
    public int MaxQueueDepthPerTenant { get; init; } = DefaultMaxQueueDepthPerTenant;

    /// <summary>The max time a run may wait in the admission queue before terminating with <c>queue_wait_exceeded</c>
    /// (CD-16), in milliseconds; 0 disables the bound. A global knob (no per-tenant override — the seam is the depth override
    /// plus this deployment default).</summary>
    public int MaxQueueWaitMs { get; init; } = DefaultMaxQueueWaitMs;

    /// <summary>The synchronous-run wall-clock window in milliseconds (CD-15): how long a default (non-<c>async</c>)
    /// <c>POST /runs</c> may hold the caller's HTTP connection before its still-executing run is <b>auto-upgraded to async</b>
    /// (the caller gets <c>202 { runId, status:"running" }</c> and polls, exactly as if <c>async:true</c> had been sent).
    /// A run finishing inside the window keeps today's synchronous <see cref="Contracts.Runs.RunResponse"/>, byte-for-byte.
    /// The 120 s default sits under every Azure ingress ceiling (Front Door / Container Apps Envoy 240 s, App Service ~230 s),
    /// so the connection is always answered — as a result or an upgrade — before ingress can kill it; a deployment tunes it
    /// to its front door (well over the longest reasonable sync job, comfortably under the ingress cap). 0 upgrades every
    /// sync run immediately (async-only posture).</summary>
    public int SyncUpgradeThresholdMs { get; init; } = DefaultSyncUpgradeThresholdMs;

    /// <summary>Projects the four mid-run caps into the interpreter's <see cref="RunLimits"/>.</summary>
    internal RunLimits ToRunLimits() =>
        new(MaxStepsPerRun, MaxDownloadedBytesPerRun, MaxEventsPerRun, ExpressionStepBudget);
}
