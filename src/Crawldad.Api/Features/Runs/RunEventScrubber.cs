using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Runs;

namespace Crawldad.Api.Features.Runs;

/// <summary>The event-sink chokepoint: scrubs a trace event's credential-prone strings just before it is appended to the
/// run's Marten stream, so nothing credential-bearing is ever persisted. Centralised via the single
/// <see cref="CredentialScrubber"/> — the RunTimeline/summary projections derive purely from these already-scrubbed events.</summary>
internal static class RunEventScrubber
{
    /// <summary>Returns a scrubbed copy of a trace event (events with no credential-prone field pass through unchanged).</summary>
    public static object Scrub(object traceEvent, CredentialScrubber scrubber) => traceEvent switch
    {
        // RunStarted records only the payload name + input key NAMES (values are never persisted), but a caller could
        // name either with credential material — scrub both defensively.
        RunStarted started => started with
        {
            PayloadName = scrubber.Scrub(started.PayloadName),
            InputKeys = [.. started.InputKeys.Select(scrubber.Scrub)],
        },

        // RunQueued is a run's opening event when it queues at the cap; it records the same payload name + input key
        // NAMES as RunStarted, so scrub both defensively for the same reason.
        RunQueued queued => queued with
        {
            PayloadName = scrubber.Scrub(queued.PayloadName),
            InputKeys = [.. queued.InputKeys.Select(scrubber.Scrub)],
        },

        // A log node can interpolate an input/extracted value into its ${…} message — the primary in-event leak vector.
        LogEmitted log => log with { Message = scrubber.Scrub(log.Message) },

        // Scrub every credential-prone free-text field defensively: a page can navigate to a URL bearing a credential
        // param (Navigated), a selector template could interpolate a value (Clicked), and an extracted key / value-ref,
        // a blob ref, a failure slug, or a region are all scrubbed so no sink can leak.
        Navigated navigated => navigated with { Url = scrubber.Scrub(navigated.Url) },
        Clicked clicked => clicked with { SelectorText = scrubber.Scrub(clicked.SelectorText) },

        // Filled carries `secret:<refName>` — a reference name, safe by construction — but scrub defensively so even a
        // ref name that collides with a registered secret cannot surface. The resolved secret is never in this event.
        Filled filled => filled with { Target = scrubber.Scrub(filled.Target) },
        Extracted extracted => extracted with { Key = scrubber.Scrub(extracted.Key), ValueRef = scrubber.Scrub(extracted.ValueRef) },
        Downloaded downloaded => downloaded with { BlobRef = scrubber.Scrub(downloaded.BlobRef) },

        // Screenshotted carries a content-addressed ref (credential-free hash) + the author's optional `name` label — the
        // ref scrubbed defensively like Downloaded.BlobRef, the name scrubbed like any author free text, null passing
        // through. The image is never in the event (only the ref), so pixels cannot leak here.
        Screenshotted shot => shot with
        {
            ScreenshotRef = scrubber.Scrub(shot.ScreenshotRef),
            Name = shot.Name is null ? null : scrubber.Scrub(shot.Name),
        },

        // Captured carries a content-addressed blob ref (a credential-free hash), scrubbed defensively like
        // Downloaded.BlobRef. The captured HTML is NEVER in the event (only the ref), so page content cannot leak here —
        // and the bytes themselves never pass through this scrubber at all (they stream straight to the tenant's storage).
        Captured captured => captured with { BlobRef = scrubber.Scrub(captured.BlobRef) },

        // SelectorMiss carries the declared selector text (a payload identifier, never page content) — scrub it
        // defensively like Clicked.SelectorText, since a selector template could interpolate a credential-shaped value.
        SelectorMiss miss => miss with { Selector = scrubber.Scrub(miss.Selector) },

        // CaptureRef is scrubbed IDENTICALLY to its Captured.BlobRef twin so the explicit failure→captures[] correlation
        // (issue #101) stays byte-exact; ScreenshotRef is left as-is — the sole carrier of the screenshot's fetch key.
        StepFailed failed => failed with { Error = scrubber.Scrub(failed.Error), CaptureRef = failed.CaptureRef is null ? null : scrubber.Scrub(failed.CaptureRef) },
        RunSessionOpened opened => opened with { Region = scrubber.Scrub(opened.Region) },

        // RunSucceeded/RunAttemptFailed/RunConnectAttemptFailed (stats or attempt-number + fixed slug), and StepStarted/
        // Waited (index/kind/ms only) carry no credential-prone free text — pass through unchanged (same instance) so
        // nothing new is allocated.
        _ => traceEvent,
    };

    /// <summary>Returns a copy of a run failure with its message scrubbed (shared by the <c>RunFailed</c> event and the HTTP response).</summary>
    public static RunFailureDetail ScrubFailure(RunFailureDetail failure, CredentialScrubber scrubber) =>
        failure with { Message = scrubber.Scrub(failure.Message) };
}
