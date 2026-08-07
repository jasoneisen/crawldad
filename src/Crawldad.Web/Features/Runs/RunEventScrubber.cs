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

        // A log node can interpolate an input/extracted value into its ${…} message — the primary in-event leak vector.
        LogEmitted log => log with { Message = scrubber.Scrub(log.Message) },

        // RunSucceeded (stats only) and RunAttemptFailed (a fixed code slug) carry no credential-prone free text.
        _ => traceEvent,
    };

    /// <summary>Returns a copy of a run failure with its message scrubbed (shared by the <c>RunFailed</c> event and the HTTP response, §10).</summary>
    /// <param name="failure">The typed failure whose message could interpolate a value via a <c>fail</c> template.</param>
    /// <param name="scrubber">The credential scrubber.</param>
    /// <returns>The failure with a scrubbed message.</returns>
    public static RunFailureDetail ScrubFailure(RunFailureDetail failure, CredentialScrubber scrubber) =>
        failure with { Message = scrubber.Scrub(failure.Message) };
}
