namespace Crawldad.Web.Features.Runs;

// Semantic step-trace events: the interpreter emits one per meaningful action (not per micro-op) through the durable-
// execution observer, so the run's Marten stream IS the trace. Appended ONLY on the background executor path — the
// synchronous POST /runs path emits none. Each is scrubbed at the RunEventScrubber chokepoint before persistence.

/// <summary>The run's backend session opened: carries the backend <see cref="Region"/> so <c>RunTimeline</c> surfaces
/// it. Emitted once, right after the page is bound, before the first step — so a resumed run (which re-connects a
/// fresh session) records its region again on resume.</summary>
public sealed record RunSessionOpened(string Region, DateTimeOffset At);

/// <summary>A top-level step began: the coarse spine of the timeline, one per top-level step (never per node inside a
/// loop body — the finer <c>Navigated</c>/<c>Clicked</c>/… events fall under it). Its timestamp drives the step's
/// duration in <c>RunTimeline</c>.</summary>
public sealed record StepStarted(int Index, string Kind, DateTimeOffset At);

/// <summary>A <c>goto</c> navigation completed. The URL is scrubbed: a page can be navigated to a URL carrying a
/// credential query param.</summary>
public sealed record Navigated(string Url, DateTimeOffset At);

/// <summary>A <c>click</c> fired: records the node's declared selector text (the raw, un-rendered selector — scrubbed
/// defensively), not the matched element.</summary>
public sealed record Clicked(string SelectorText, DateTimeOffset At);

/// <summary>A secret was typed into a form field: emitted by <c>fill.secret</c> so the trace records that a login field
/// was filled — <b>never the secret</b>. <see cref="Target"/> is <c>secret:&lt;refName&gt;</c>, so the resolved value is
/// structurally absent from the event by construction, not merely scrubbed after the fact.</summary>
public sealed record Filled(string Target, DateTimeOffset At);

/// <summary>A wait completed: a <c>waitFor</c>/<c>waitForLoadState</c>/<c>waitForRequest</c>. <see cref="Kind"/> names
/// what was awaited (e.g. <c>selector:visible</c>/<c>loadState:networkidle</c>/<c>request</c>); <see cref="Ms"/> is the
/// elapsed wait (0 under a frozen test clock).</summary>
public sealed record Waited(string Kind, long Ms, DateTimeOffset At);

/// <summary>A value was bound into the run's data model: a <c>set</c> or <c>push</c>. PII-safe: <see cref="ValueRef"/>
/// is a shape descriptor (kind + size), <b>never</b> the raw value; <see cref="Key"/> is the target var/list name (a
/// static payload identifier, scrubbed defensively).</summary>
public sealed record Extracted(string Key, string ValueRef, DateTimeOffset At);

/// <summary>A <c>download</c> streamed to blob storage. Metadata only: the content-addressed blob ref, the content type
/// guessed from the stored name, the byte size, and the SHA-256 — never the bytes.</summary>
public sealed record Downloaded(string BlobRef, string ContentType, long Size, string Sha256, DateTimeOffset At);

/// <summary>An explicit <c>screenshot</c> node captured the page: the author-authored analogue of screenshot-on-failure,
/// through the <b>same</b> <c>IScreenshotStore</c> seam. Metadata only — the content-addressed ref and PNG byte size,
/// never the image. Unlike the best-effort failure capture, a faulting explicit capture surfaces as the run's failure.</summary>
public sealed record Screenshotted(string ScreenshotRef, string? Name, long Size, DateTimeOffset At);

/// <summary>A <c>capture</c> node (or capture-on-failure) serialised a document to the tenant's BYO storage. Metadata
/// only: the content-addressed blob ref, the byte size, and the SHA-256 — <b>never the HTML</b>, which streams straight
/// to the customer's own storage target and never through the credential scrubber (so a captured page is byte-faithful).</summary>
public sealed record Captured(string BlobRef, long Size, string Sha256, DateTimeOffset At);

/// <summary>A step failed: emitted just before the run reports a terminal / retryable-exhausted failure, so the trace
/// pinpoints the failing step and links its failure artifacts. <see cref="ScreenshotRef"/> is null when no screenshot was
/// captured (disabled, no page bound, or a forcibly-cancelled deadline) — the image lives in blob storage, only the ref
/// here. <see cref="CaptureRef"/> is the failing page's <c>config.captureOnFailure</c> HTML document ref — the same
/// content-addressed ref its <see cref="Captured"/> twin folds into the timeline's <c>captures[]</c>, so the failure
/// links its captured page explicitly rather than by position; null when capture-on-failure is disabled or captured
/// nothing (no observer/page, or a crashed page's serialisation was tolerated).</summary>
public sealed record StepFailed(int Index, string Error, string? ScreenshotRef, string? CaptureRef, DateTimeOffset At);

/// <summary>An extraction selector matched no element (the soft-mode drift signal for #47): a <c>text</c>/<c>innerText</c>/
/// <c>innerHtml</c>/<c>attr</c> whose target found zero matches — distinct from a matched-but-empty element (legitimately
/// blank data, never a miss). Emitted once per distinct <see cref="Selector"/> per run (repeats only advance
/// <c>stats.selectorMisses</c>), so a drifted selector hit across every row of a loop is one event, not thousands. The
/// run stays <b>successful</b> — the counter/event are visible while it succeeds — unless the extraction was
/// <c>require(...)</c>-wrapped or <c>config.strictExtraction</c> is set, which raise a terminal <c>selector_miss</c>
/// (a <see cref="StepFailed"/>) instead. <see cref="Selector"/> is the declared selector text (scrubbed defensively),
/// never page content; <see cref="StepIndex"/> is the enclosing top-level step.</summary>
public sealed record SelectorMiss(string Selector, int StepIndex, DateTimeOffset At);
