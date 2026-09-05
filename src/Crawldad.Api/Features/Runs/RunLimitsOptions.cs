using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Api.Features.Runs;

/// <summary>The server-side resource-limit knobs, bound from <c>Crawldad:Limits</c>. A payload can never raise them —
/// they are not payload fields. The four mid-run caps flow into <see cref="RunLimits"/> for the interpreter;
/// <see cref="MaxConcurrentRunsPerTenant"/> is the admission cap <see cref="RunAdmissionGate"/> enforces (per-tenant override via <see cref="Crawldad.Api.Infrastructure.Security.TenantDescriptor.MaxConcurrentRuns"/>).</summary>
public sealed class RunLimitsOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Crawldad:Limits";

    /// <summary>The global default concurrent-run cap per tenant when a tenant sets no override.</summary>
    public const int DefaultMaxConcurrentRunsPerTenant = 32;

    /// <summary>The global default admission-queue depth per tenant when a tenant sets no override — the number of
    /// at-cap runs that may wait before a further one is rejected <c>429 queue_depth_exceeded</c>. Generous by default
    /// so the queue is effectively unbounded for legitimate use; the per-tier value is set per tenant.</summary>
    public const int DefaultMaxQueueDepthPerTenant = 1_000;

    /// <summary>The global default max queue wait in milliseconds: how long a run may sit queued before it terminates
    /// with <c>queue_wait_exceeded</c>. 0 disables the bound (a queued run waits indefinitely for a slot); the default
    /// leaves it disabled so time-in-queue is capped only where a deployment opts in.</summary>
    public const int DefaultMaxQueueWaitMs = 0;

    /// <summary>The default synchronous-run wall-clock window in milliseconds: how long a default (non-<c>async</c>)
    /// <c>POST /runs</c> may hold the connection before auto-upgrading to async (<c>202</c>, polled thereafter). 120 s
    /// sits comfortably under every Azure ingress ceiling (Front Door/Container Apps Envoy 240 s, App Service ~230 s).</summary>
    public const int DefaultSyncUpgradeThresholdMs = 120 * 1000;

    /// <summary>The default host-shutdown drain window in milliseconds: how long <see cref="SyncRunSupervisor"/> waits for
    /// in-flight upgraded-run tails to finalise before letting the host dispose the provider. 15 s sits comfortably under
    /// the host's default 30 s shutdown timeout, so the drain finishes gracefully rather than being killed with it.</summary>
    public const int DefaultShutdownDrainMs = 15 * 1000;

    /// <summary>The most semantic steps one execution may run — <c>max_steps_exceeded</c> beyond it.</summary>
    public int MaxStepsPerRun { get; init; } = RunLimits.DefaultMaxSteps;

    /// <summary>The most bytes one execution may download in total — <c>max_download_bytes_exceeded</c> beyond it.</summary>
    public long MaxDownloadedBytesPerRun { get; init; } = RunLimits.DefaultMaxDownloadedBytes;

    /// <summary>The most bytes one execution may <c>capture</c> (serialised documents streamed to tenant storage) in
    /// total — <c>max_capture_bytes_exceeded</c> beyond it. A sibling of <see cref="MaxDownloadedBytesPerRun"/>: the two
    /// channels are budgeted separately so a document-capture workload and a file-download workload tune independently.</summary>
    public long MaxCapturedBytesPerRun { get; init; } = RunLimits.DefaultMaxCapturedBytes;

    /// <summary>The most trace events one execution may append to its run stream — <c>max_events_exceeded</c> beyond it.</summary>
    public int MaxEventsPerRun { get; init; } = RunLimits.DefaultMaxEvents;

    /// <summary>The per-evaluation expression fuel budget — <c>expression_budget_exceeded</c> beyond it.</summary>
    public int ExpressionStepBudget { get; init; } = CrawldadExpression.DefaultStepBudget;

    /// <summary>The global default concurrent, non-terminal runs a tenant may have in flight — a per-tenant override
    /// takes precedence when configured. At the cap, <c>POST /runs</c> queues the run (<c>202 status:"queued"</c>)
    /// rather than rejecting it.</summary>
    public int MaxConcurrentRunsPerTenant { get; init; } = DefaultMaxConcurrentRunsPerTenant;

    /// <summary>The global default admission-queue depth per tenant — a per-tenant override
    /// (<see cref="Crawldad.Api.Infrastructure.Security.TenantDescriptor.MaxQueueDepth"/>) takes precedence. At this
    /// depth a further at-cap run is rejected 429 <c>queue_depth_exceeded</c> — the only 429 admission still returns.</summary>
    public int MaxQueueDepthPerTenant { get; init; } = DefaultMaxQueueDepthPerTenant;

    /// <summary>The max time a run may wait in the admission queue before terminating with <c>queue_wait_exceeded</c>,
    /// in milliseconds; 0 disables the bound. A global knob (no per-tenant override — the seam is the depth override
    /// plus this deployment default).</summary>
    public int MaxQueueWaitMs { get; init; } = DefaultMaxQueueWaitMs;

    /// <summary>The synchronous-run wall-clock window in milliseconds: how long a default (non-<c>async</c>)
    /// <c>POST /runs</c> may hold the connection before its still-executing run is auto-upgraded to async (<c>202</c>,
    /// polled thereafter). A run finishing inside the window keeps today's synchronous response, byte-for-byte. 0 upgrades every sync run immediately.</summary>
    public int SyncUpgradeThresholdMs { get; init; } = DefaultSyncUpgradeThresholdMs;

    /// <summary>How long host shutdown drains in-flight upgraded-run tails before disposing the provider, in
    /// milliseconds; 0 means no drain window at all (every in-flight tail is reported and left for the next host's
    /// startup recovery). Kept under the host's shutdown timeout so a stuck run can never hang teardown.</summary>
    public int ShutdownDrainMs { get; init; } = DefaultShutdownDrainMs;

    /// <summary>Projects the mid-run caps into the interpreter's <see cref="RunLimits"/>.</summary>
    internal RunLimits ToRunLimits() =>
        new(MaxStepsPerRun, MaxDownloadedBytesPerRun, MaxCapturedBytesPerRun, MaxEventsPerRun, ExpressionStepBudget);
}
