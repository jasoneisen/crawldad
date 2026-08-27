using Crawldad.Contracts.Runs;

namespace Crawldad.Portal.Live;

/// <summary>Pure presentation helpers for the live-trace page: the trace event → Tabler colour class, the running
/// status a frame implies, the terminal-frame headline, and the data-cell preview. Kept out of the <c>.razor</c> markup
/// so every branch is unit-testable directly (the component stays thin and the 100% gate is met without threading dozens
/// of frame permutations through bUnit). The event names are the API's CLR trace-event type names, streamed verbatim as
/// the SSE <c>event:</c> field (see <c>RunTraceEvents</c>/<c>RunEvents</c> in the API).</summary>
internal static class LiveView
{
    /// <summary>The longest data preview rendered inline in a feed row before it is elided — a single scanning line, the
    /// full (already-scrubbed) body stays a click away on the run-detail surface.</summary>
    internal const int MaxPreview = 160;

    /// <summary>The feed-row class for a trace event: <c>evt</c> plus the colour modifier the mockup assigns each event
    /// family (navigation blue, extraction green, miss/failure warn/red, capture purple, …). An unmapped or lifecycle
    /// event falls to the neutral step tone, so a new API event type renders cleanly rather than unstyled.</summary>
    public static string EventCss(string eventType) => eventType switch
    {
        "Navigated" => "evt e-nav",
        "Clicked" => "evt e-click",
        "Waited" => "evt e-wait",
        "Extracted" => "evt e-extract",
        "Filled" => "evt e-fill",
        "SelectorMiss" => "evt e-miss",
        "Screenshotted" => "evt e-shot",
        "Captured" => "evt e-cap",
        "LogEmitted" => "evt e-log",
        "StepFailed" or "RunFailed" => "evt e-fail",
        _ => "evt e-step",
    };

    /// <summary>The run disposition a freshly-seen frame implies, folded onto the current one: a queue frame reads
    /// <see cref="RunStatus.Queued"/>; a start/dequeue/resume/step frame reads <see cref="RunStatus.Running"/>; a
    /// terminal frame reads its terminal status; anything else leaves the status unchanged. Drives the header badge as
    /// the stream advances.</summary>
    public static RunStatus StatusFor(RunStatus current, string eventType) => eventType switch
    {
        "RunQueued" => RunStatus.Queued,
        "RunStarted" or "RunDequeued" or "RunResumed" or "StepStarted" => RunStatus.Running,
        "RunSucceeded" => RunStatus.Succeeded,
        "RunFailed" => RunStatus.Failed,
        "RunCancelled" => RunStatus.Cancelled,
        _ => current,
    };

    /// <summary>The completion headline for a finished run — shown in the terminal banner once the stream closes (or the
    /// timeline fallback resolves). A still-open disposition falls to a neutral "finished" label (never rendered in
    /// practice, but keeps the mapping total).</summary>
    public static string CompletionLabel(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "Run succeeded",
        RunStatus.Failed => "Run failed",
        RunStatus.Cancelled => "Run cancelled",
        _ => "Run finished",
    };

    /// <summary>The single-line data preview for a feed row: the frame's (already-scrubbed) JSON body trimmed, and
    /// elided with an ellipsis past <see cref="MaxPreview"/> so one long body never breaks the row rhythm.</summary>
    public static string Preview(string data)
    {
        var trimmed = data.Trim();
        return trimmed.Length <= MaxPreview ? trimmed : string.Concat(trimmed.AsSpan(0, MaxPreview), "…");
    }
}
