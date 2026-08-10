namespace Crawldad.Contracts.Runs;

/// <summary>The <c>GET /runs/{id}/drift</c> response: a run's pinned payload revision vs. the payload's current head.
/// Equal <see cref="PinnedScriptHash"/>/<see cref="HeadScriptHash"/> under a revision mismatch means the head moved by
/// a metadata-only change (rename/archive), not a script revise. An inline run never drifts.</summary>
public sealed record RunDriftResponse(
    Guid RunId,
    Guid? PayloadId,
    int? PinnedRevision,
    string PinnedScriptHash,
    int? HeadRevision,
    string? HeadScriptHash,
    bool Drifted);
