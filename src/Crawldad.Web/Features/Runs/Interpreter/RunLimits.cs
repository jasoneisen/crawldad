using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>The server-side per-run resource caps the interpreter enforces mid-run. Each is a terminal failure when
/// exceeded; none is a payload field, so a payload can never raise them. Caps reset per execution segment (a resume
/// builds a fresh interpreter), but accumulate across retries of the same interpreter (the more conservative guard).</summary>
internal sealed record RunLimits(int MaxSteps, long MaxDownloadedBytes, long MaxCapturedBytes, int MaxEvents, int ExpressionStepBudget)
{
    /// <summary>The default max-steps cap: generous headroom over any legitimate crawl (hundreds–low-thousands of steps).</summary>
    public const int DefaultMaxSteps = 100_000;

    /// <summary>The default max-downloaded-bytes cap: 1 GiB, far above any legitimate single-run download volume.</summary>
    public const long DefaultMaxDownloadedBytes = 1L << 30;

    /// <summary>The default max-captured-bytes cap: 1 GiB, the run-wide total a <c>capture</c> channel may stream to
    /// tenant storage. A sibling of the download cap, not shared with it — a document-capture crawl and a file-download
    /// crawl have very different byte profiles, so each channel is bounded (and reported) independently.</summary>
    public const long DefaultMaxCapturedBytes = 1L << 30;

    /// <summary>The default max-events cap: a generous fair-use guardrail invisible to ~all runs.</summary>
    public const int DefaultMaxEvents = 100_000;

    /// <summary>The generous defaults, used by the interpreter unit harness and any interpreter constructed without an
    /// explicit config (the production paths always pass the resolved <see cref="RunLimitsOptions"/> values).</summary>
    public static RunLimits Default { get; } =
        new(DefaultMaxSteps, DefaultMaxDownloadedBytes, DefaultMaxCapturedBytes, DefaultMaxEvents, CrawldadExpression.DefaultStepBudget);
}
