namespace Crawldad.Portal.Components.Pages.App;

/// <summary>The replay-run form model (the static-SSR form post on the run-detail page). The single
/// <see cref="Inputs"/> field is the resupplied run inputs as a JSON object: input <em>values</em> are never persisted
/// with a run (only redacted key <em>names</em> survive on the timeline), so a replay cannot recover them and the caller
/// resupplies them here. It is nullable so a blank submit round-trips as empty rather than tripping the framework binder,
/// and the page treats it as <b>write-only</b> — cleared after each submit so submitted inputs (which may carry secrets,
/// e.g. a backend <c>credentialRef</c>) are never echoed back into the rendered field.</summary>
public sealed class ReplayRunInput
{
    /// <summary>The resupplied run inputs as a JSON object (or blank for none). Write-only in the UI.</summary>
    public string? Inputs { get; set; }
}
