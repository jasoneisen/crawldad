using JasperFx.Events.Projections;
using Marten;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// Self-registration for the Payloads slice (§14.4). A payload is an event-sourced aggregate whose stream is its
/// version history; Phase 5 fills this in with <c>PayloadDrafted</c>/<c>PayloadRevised</c>/… events, the
/// <c>Payload</c> snapshot, and the <c>PayloadSummary</c> read model — all on the shared projection lifecycle,
/// exactly as <c>IncidentModule</c> does in the foundation. Kept as a registered-but-empty stub so the wiring
/// seam exists now and the later package fills it in place.
/// </summary>
public static class PayloadsModule
{
    /// <summary>
    /// Registers the slice's events and projections on the shared <paramref name="lifecycle"/> (Async in
    /// production, Inline under the test switch). Nothing to register until Phase 5.
    /// </summary>
    public static void ConfigureMarten(StoreOptions options, ProjectionLifecycle lifecycle)
    {
        // No events, snapshots, or projections yet. Discard the seam parameters so this empty stub stays
        // warning-clean under the zero-warning gate; Phase 5 replaces these lines with the real registrations.
        _ = options;
        _ = lifecycle;
    }
}
