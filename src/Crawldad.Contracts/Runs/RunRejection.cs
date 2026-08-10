namespace Crawldad.Contracts.Runs;

/// <summary>A <c>POST /runs</c> request-level rejection — distinct from a run failure (a started-then-faulted run is
/// still HTTP 200): no run is ever started. <b>400</b> for an unrunnable pinned-payload reference (unknown
/// payload/revision, archived); <b>429</b> when the tenant's admission queue is at its per-tier depth.</summary>
public sealed record RunRejection(string Code, string Message);
