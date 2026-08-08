using Crawldad.Contracts.Runs;
using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The event-sink chokepoint (§12, WP3): scrubs a trace event's credential-prone strings just before it is appended to
/// the run's Marten stream, so <b>nothing credential-bearing is ever persisted</b>. Centralised here (rather than at each
/// <c>Append</c> call site) via the single <see cref="CredentialScrubber"/> — the <c>RunTimeline</c>/summary projections
/// (Phase 5) derive purely from these already-scrubbed events, so they inherit the guarantee by construction and need no
/// scrubbing of their own.
/// </summary>
internal static class RunEventScrubber
{
    /// <summary>Returns a scrubbed copy of a trace event (events with no credential-prone field pass through unchanged).</summary>
    /// <param name="traceEvent">The event about to be appended (<c>RunStarted</c>/<c>LogEmitted</c>/<c>RunAttemptFailed</c>/…).</param>
    /// <param name="scrubber">The credential scrubber.</param>
    /// <returns>The event with its credential-prone strings scrubbed.</returns>
    public static object Scrub(object traceEvent, CredentialScrubber scrubber) => traceEvent switch
    {
        // RunStarted records only the payload name + input key NAMES (values are never persisted, §12), but a caller
        // could name either with credential material — scrub both defensively.
        RunStarted started => started with
        {
            PayloadName = scrubber.Scrub(started.PayloadName),
            InputKeys = [.. started.InputKeys.Select(scrubber.Scrub)],
        },

        // RunQueued (CD-16) is a run's opening event when it queues at the cap; it records the same payload name + input key
        // NAMES as RunStarted, so scrub both defensively for the same reason.
        RunQueued queued => queued with
        {
            PayloadName = scrubber.Scrub(queued.PayloadName),
            InputKeys = [.. queued.InputKeys.Select(scrubber.Scrub)],
        },

        // A log node can interpolate an input/extracted value into its ${…} message — the primary in-event leak vector.
        LogEmitted log => log with { Message = scrubber.Scrub(log.Message) },

        // The WP3 step-trace events (§13): scrub every credential-prone free-text field defensively. A page can navigate
        // to a URL bearing a credential param (Navigated), a selector template could interpolate a value (Clicked), and
        // an extracted key / value-ref, a blob ref, a failure slug, or a region are all scrubbed so no sink can leak.
        Navigated navigated => navigated with { Url = scrubber.Scrub(navigated.Url) },
        Clicked clicked => clicked with { SelectorText = scrubber.Scrub(clicked.SelectorText) },
        Extracted extracted => extracted with { Key = scrubber.Scrub(extracted.Key), ValueRef = scrubber.Scrub(extracted.ValueRef) },
        Downloaded downloaded => downloaded with { BlobRef = scrubber.Scrub(downloaded.BlobRef) },
        StepFailed failed => failed with { Error = scrubber.Scrub(failed.Error) },
        RunSessionOpened opened => opened with { Region = scrubber.Scrub(opened.Region) },

        // RunSucceeded/RunAttemptFailed (stats + fixed slug), and StepStarted/Waited (index/kind/ms only) carry no
        // credential-prone free text — pass through unchanged (same instance) so nothing new is allocated.
        _ => traceEvent,
    };

    /// <summary>Returns a copy of a run failure with its message scrubbed (shared by the <c>RunFailed</c> event and the HTTP response, §10).</summary>
    /// <param name="failure">The typed failure whose message could interpolate a value via a <c>fail</c> template.</param>
    /// <param name="scrubber">The credential scrubber.</param>
    /// <returns>The failure with a scrubbed message.</returns>
    public static RunFailureDetail ScrubFailure(RunFailureDetail failure, CredentialScrubber scrubber) =>
        failure with { Message = scrubber.Scrub(failure.Message) };
}
