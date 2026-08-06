namespace Crawldad.Contracts.Runs;

/// <summary>The terminal disposition of a run in the §10 response. Serialized camelCase (<c>"succeeded"</c>/<c>"failed"</c>)
/// via <see cref="ContractsJson"/>. <c>cancelled</c> arrives with the cancellation control channel in Phase 5.</summary>
public enum RunStatus
{
    /// <summary>The run completed and produced a <c>result</c>.</summary>
    Succeeded,

    /// <summary>The run raised a typed failure (§8.3); the response carries <c>failure</c> instead of <c>result</c>.</summary>
    Failed,
}
