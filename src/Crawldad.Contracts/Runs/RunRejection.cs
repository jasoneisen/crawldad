namespace Crawldad.Contracts.Runs;

/// <summary>A run control-surface rejection returned as a typed body — distinct from a run failure (a started-then-faulted
/// run is still HTTP 200). On <c>POST /runs</c>//<c>replay</c> no run is ever started: <b>400</b> for an unrunnable
/// pinned-payload reference (unknown payload/revision, archived), <b>429</b> when the tenant's admission queue is at its
/// per-tier depth. On <c>DELETE /runs/{id}</c>: <b>409</b> <c>run_still_active</c> when the target run has not finished
/// (cancel it before erasing).</summary>
public sealed record RunRejection(string Code, string Message);
