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

    /// <summary>The most semantic steps one execution may run (§12) — <c>max_steps_exceeded</c> beyond it.</summary>
    public int MaxStepsPerRun { get; init; } = RunLimits.DefaultMaxSteps;

    /// <summary>The most bytes one execution may download in total (§9.3/§12) — <c>max_download_bytes_exceeded</c> beyond it.</summary>
    public long MaxDownloadedBytesPerRun { get; init; } = RunLimits.DefaultMaxDownloadedBytes;

    /// <summary>The most trace events one execution may append to its run stream (§12) — <c>max_events_exceeded</c> beyond it.</summary>
    public int MaxEventsPerRun { get; init; } = RunLimits.DefaultMaxEvents;

    /// <summary>The per-evaluation expression fuel budget (§7.2/§12) — <c>expression_budget_exceeded</c> beyond it.</summary>
    public int ExpressionStepBudget { get; init; } = CrawldadExpression.DefaultStepBudget;

    /// <summary>The global default concurrent, non-terminal runs a tenant may have in flight (docs/PRODUCT.md §Pv.3) — a
    /// per-tenant override takes precedence when configured. At the cap, <c>POST /runs</c> is rejected 429
    /// <c>concurrent_runs_exceeded</c> (CD-16 will upgrade reject → queue).</summary>
    public int MaxConcurrentRunsPerTenant { get; init; } = DefaultMaxConcurrentRunsPerTenant;

    /// <summary>Projects the four mid-run caps into the interpreter's <see cref="RunLimits"/>.</summary>
    internal RunLimits ToRunLimits() =>
        new(MaxStepsPerRun, MaxDownloadedBytesPerRun, MaxEventsPerRun, ExpressionStepBudget);
}
