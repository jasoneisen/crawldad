namespace Crawldad.Web.Features.Runs;

// The Phase 5 WP3 semantic step-trace events (§13): as the interpreter executes, it emits one event per meaningful action
// — not per micro-op — through the durable-execution observer, so the run's Marten stream IS the trace and an SSE tail /
// the RunTimeline projection render from it. They are appended ONLY on the background executor path (an observer is
// present); the synchronous POST /runs path emits none, so its stream — and every §10 golden — is byte-identical to
// before. Each is scrubbed at the RunEventScrubber chokepoint before it is persisted (§12), so nothing credential-bearing
// or bulk-PII ever lands: Extracted carries a shape ref, never the value; Downloaded carries blob metadata, never bytes;
// StepFailed carries a screenshot ref, never the image. Volume is bounded (one stream per run, semantic granularity — the
// 50-page loop is still only hundreds, §12).

/// <summary>
/// The run's backend session opened (§9.1): carries the backend <see cref="Region"/> so the <c>RunTimeline</c> surfaces it
/// (§13) without churning the §10 <c>RunResponse</c> goldens. Emitted once, right after the page is bound, before the first
/// step — so a resumed run (which re-connects a fresh session) records its region again on resume.
/// </summary>
/// <param name="Region">The backend region the session runs in (the fake reports <c>fake</c>).</param>
/// <param name="At">When the session opened (through the <see cref="TimeProvider"/> seam).</param>
public sealed record RunSessionOpened(string Region, DateTimeOffset At);

/// <summary>A top-level step began (§13 <c>StepStarted</c>): the coarse spine of the timeline, one per top-level step (never
/// per node inside a loop body — the finer <c>Navigated</c>/<c>Clicked</c>/… events fall under it). Its timestamp drives the
/// step's duration in the <c>RunTimeline</c>.</summary>
/// <param name="Index">The top-level step index.</param>
/// <param name="Kind">The step's head kind (e.g. <c>goto</c>/<c>loop</c>).</param>
/// <param name="At">When the step started (through the <see cref="TimeProvider"/> seam).</param>
public sealed record StepStarted(int Index, string Kind, DateTimeOffset At);

/// <summary>A <c>goto</c> navigation completed (§13 <c>Navigated</c>). The URL is scrubbed (§12): a page can be navigated to a
/// URL carrying a credential query param.</summary>
/// <param name="Url">The URL navigated to (scrubbed).</param>
/// <param name="At">When the navigation completed (through the <see cref="TimeProvider"/> seam).</param>
public sealed record Navigated(string Url, DateTimeOffset At);

/// <summary>A <c>click</c> fired (§13 <c>Clicked</c>): records the node's declared selector text (the raw, un-rendered
/// selector — scrubbed defensively, §12), not the matched element.</summary>
/// <param name="SelectorText">The click node's selector text (scrubbed).</param>
/// <param name="At">When the click fired (through the <see cref="TimeProvider"/> seam).</param>
public sealed record Clicked(string SelectorText, DateTimeOffset At);

/// <summary>
/// A secret was typed into a form field (§13 <c>Filled</c>/CD-6): emitted by a <c>fill.secret</c> so the trace records that a
/// login field was filled — <b>never the secret</b>. <see cref="Target"/> is <c>secret:&lt;refName&gt;</c> (the secretRef input
/// name, which is safe — it is a reference, not the secret), so the resolved value is structurally absent from the event by
/// construction, not merely scrubbed after the fact. Emitted only on the durable executor path (an observer is present); a
/// plain value <c>fill</c> emits no trace event, so existing runs are unchanged.
/// </summary>
/// <param name="Target">The fill target descriptor — <c>secret:&lt;refName&gt;</c>, the secretRef input name (never the value).</param>
/// <param name="At">When the field was filled (through the <see cref="TimeProvider"/> seam).</param>
public sealed record Filled(string Target, DateTimeOffset At);

/// <summary>A wait completed (§13 <c>Waited</c>): a <c>waitFor</c>/<c>waitForLoadState</c>/<c>waitForRequest</c>. <see cref="Kind"/>
/// names what was awaited (e.g. <c>selector:visible</c>/<c>loadState:networkidle</c>/<c>request</c>); <see cref="Ms"/> is the
/// elapsed wait (through the clock seam — 0 under a frozen test clock).</summary>
/// <param name="Kind">What was awaited.</param>
/// <param name="Ms">How long the wait took, in milliseconds.</param>
/// <param name="At">When the wait completed (through the <see cref="TimeProvider"/> seam).</param>
public sealed record Waited(string Kind, long Ms, DateTimeOffset At);

/// <summary>A value was bound into the run's data model (§13 <c>Extracted</c>): a <c>set</c> or <c>push</c>. PII-safe (§12):
/// <see cref="ValueRef"/> is a shape descriptor (kind + size), <b>never</b> the raw value; <see cref="Key"/> is the target
/// var/list name (a static payload identifier, scrubbed defensively).</summary>
/// <param name="Key">The target var / list name (scrubbed).</param>
/// <param name="ValueRef">A shape descriptor of the bound value (scrubbed) — never the value itself.</param>
/// <param name="At">When the value was bound (through the <see cref="TimeProvider"/> seam).</param>
public sealed record Extracted(string Key, string ValueRef, DateTimeOffset At);

/// <summary>A <c>download</c> streamed to blob storage (§13 <c>Downloaded</c>/§9.3). Metadata only (§12): the content-addressed
/// blob ref, the content type guessed from the stored name, the byte size, and the SHA-256 — never the bytes.</summary>
/// <param name="BlobRef">The engine's stored-blob name (content-addressed; scrubbed defensively).</param>
/// <param name="ContentType">The content type guessed from the stored name's extension.</param>
/// <param name="Size">The downloaded byte count.</param>
/// <param name="Sha256">The full SHA-256 of the bytes (lowercase hex).</param>
/// <param name="At">When the download completed (through the <see cref="TimeProvider"/> seam).</param>
public sealed record Downloaded(string BlobRef, string ContentType, long Size, string Sha256, DateTimeOffset At);

/// <summary>
/// An explicit <c>screenshot</c> node captured the page (§13/#8): the author-authored analogue of screenshot-on-failure,
/// flowing through the <b>same</b> <c>IScreenshotStore</c> seam, so it inherits its deletable, tenant-partitioned,
/// TTL-governed blob storage for free. Metadata only (§12): the content-addressed <see cref="ScreenshotRef"/>
/// (<c>screenshots/{sha256}.png</c>, exactly like <c>StepFailed.ScreenshotRef</c>) and the captured PNG byte size — <b>never</b>
/// the image, which lives only in the deletable blob store. <see cref="Name"/> is the node's optional author label (a rendered
/// Tmpl, scrubbed defensively), for correlating the shot in the trace/timeline; it never affects the content-addressed ref.
/// Emitted only on the durable executor path (an observer + a store are present); on the synchronous path the node is inert,
/// so the §10 goldens are unchanged. Unlike the best-effort failure capture, an explicit capture that faults surfaces as the
/// run's failure — the author asked for it.
/// </summary>
/// <param name="ScreenshotRef">The captured screenshot's content-addressed blob ref (<c>screenshots/{sha256}.png</c>; scrubbed defensively).</param>
/// <param name="Name">The node's optional author label (rendered Tmpl, scrubbed), or null when the node declared none.</param>
/// <param name="Size">The captured PNG byte count (metadata only, never the bytes).</param>
/// <param name="At">When the screenshot was captured (through the <see cref="TimeProvider"/> seam).</param>
public sealed record Screenshotted(string ScreenshotRef, string? Name, long Size, DateTimeOffset At);

/// <summary>
/// A step failed (§13 <c>StepFailed</c>): emitted just before the run reports a terminal / retryable-exhausted failure, so
/// the trace pinpoints the failing step and links its screenshot. <see cref="ScreenshotRef"/> is the failure screenshot's
/// blob ref (§13 screenshot-on-failure), or null when none was captured (screenshots disabled via
/// <c>config.screenshotOnFailure</c>, no page bound on a setup failure, or a forcibly-cancelled deadline). The image lives
/// in deletable blob storage (§12) — only the ref is in this immutable trace.
/// </summary>
/// <param name="Index">The top-level step index the failure occurred at.</param>
/// <param name="Error">The failure code slug (scrubbed defensively).</param>
/// <param name="ScreenshotRef">The failure screenshot's blob ref, or null when none was captured.</param>
/// <param name="At">When the step failed (through the <see cref="TimeProvider"/> seam).</param>
public sealed record StepFailed(int Index, string Error, string? ScreenshotRef, DateTimeOffset At);
