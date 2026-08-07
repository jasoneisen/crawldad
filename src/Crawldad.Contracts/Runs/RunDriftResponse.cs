namespace Crawldad.Contracts.Runs;

/// <summary>
/// The <c>GET /runs/{id}/drift</c> response (§14.1/§13): a run's pinned payload revision compared to the payload's
/// <b>current head</b>. Drift = the pinned revision is no longer the head revision (§14.1 "drift = pinned-vs-head") — the
/// payload has moved on since the run was pinned. The pinned and head <see cref="PinnedScriptHash"/>/<see cref="HeadScriptHash"/>
/// are reported for diagnosis: equal hashes under a revision mismatch mean the head moved by a metadata-only change (a
/// rename/archive) rather than a script revise. An <b>inline</b> run (no pinned payload) never drifts: its
/// <see cref="PayloadId"/> and head fields are null and <see cref="Drifted"/> is false.
/// </summary>
/// <param name="RunId">The run's stream id.</param>
/// <param name="PayloadId">The pinned managed payload, or null for an inline run.</param>
/// <param name="PinnedRevision">The revision the run pinned at start, or null for an inline run.</param>
/// <param name="PinnedScriptHash">The script hash the run pinned at start (SHA-256, lowercase hex).</param>
/// <param name="HeadRevision">The payload's current head revision, or null for an inline run.</param>
/// <param name="HeadScriptHash">The payload's current head script hash, or null for an inline run.</param>
/// <param name="Drifted">True when the pinned revision is not the current head revision (§14.1).</param>
public sealed record RunDriftResponse(
    Guid RunId,
    Guid? PayloadId,
    int? PinnedRevision,
    string PinnedScriptHash,
    int? HeadRevision,
    string? HeadScriptHash,
    bool Drifted);
