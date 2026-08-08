using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// The server-side per-run resource caps the interpreter enforces mid-run (CD-3/§12), beside the wall-clock deadline
/// (§8.4), the per-node <c>timeoutMs</c> hierarchy, <c>loop.maxIterations</c>, and the regex size/time guards. Each is a
/// terminal failure with its own code when exceeded; none is a payload field, so a payload can never raise them — they are
/// resolved from <see cref="RunLimitsOptions"/> once and handed to every interpreter, sync and async.
/// <para>
/// The caps are <b>per execution segment</b>: they reset when a killed run resumes from a checkpoint (each resume builds a
/// fresh interpreter, §11), because they guard a single execution's runaway while the wall-clock deadline is the cumulative
/// guard across resumes. A retry re-uses the same interpreter, so its counters accumulate across attempts (the more
/// conservative choice for a guard).
/// </para>
/// </summary>
/// <param name="MaxSteps">The most semantic steps one execution may run before <c>max_steps_exceeded</c> — loop iterations
/// multiply steps, so this bounds a runaway payload the per-loop <c>maxIterations</c> cap cannot.</param>
/// <param name="MaxDownloadedBytes">The most bytes one execution may download in total (across every <c>download</c>)
/// before <c>max_download_bytes_exceeded</c> — enforced as the bytes flow, never buffer-then-check.</param>
/// <param name="MaxEvents">The most trace events one execution may append to the run stream before
/// <c>max_events_exceeded</c> — a generous fair-use guardrail (docs/PRODUCT.md §Pv.3) no legitimate run reaches.</param>
/// <param name="ExpressionStepBudget">The per-evaluation expression fuel budget (carried onto the run scope) — the most
/// node evaluations one <c>${…}</c>/<c>Expr</c> may spend before <c>expression_budget_exceeded</c>.</param>
internal sealed record RunLimits(int MaxSteps, long MaxDownloadedBytes, int MaxEvents, int ExpressionStepBudget)
{
    /// <summary>The default max-steps cap: generous headroom over any legitimate crawl (hundreds–low-thousands of steps).</summary>
    public const int DefaultMaxSteps = 100_000;

    /// <summary>The default max-downloaded-bytes cap: 1 GiB, far above any legitimate single-run download volume.</summary>
    public const long DefaultMaxDownloadedBytes = 1L << 30;

    /// <summary>The default max-events cap: a generous fair-use guardrail (docs/PRODUCT.md §Pv.3) invisible to ~all runs.</summary>
    public const int DefaultMaxEvents = 100_000;

    /// <summary>The generous defaults, used by the interpreter unit harness and any interpreter constructed without an
    /// explicit config (the production paths always pass the resolved <see cref="RunLimitsOptions"/> values).</summary>
    public static RunLimits Default { get; } =
        new(DefaultMaxSteps, DefaultMaxDownloadedBytes, DefaultMaxEvents, CrawldadExpression.DefaultStepBudget);
}
