namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// A payload was drafted — revision 1 of a managed payload (§14.1). Each revision is one event, so the stream is the
/// version history. The full <see cref="Script"/> is stored inline (v1; large scripts would content-address the body
/// under <see cref="ScriptHash"/>, §14.1). A payload is automation data, not credentials — those are run-time
/// inputs-by-reference (§12), never part of the saved document — so the script is safe to persist. As defence in depth
/// it is nonetheless credential-scrubbed at the persistence boundary before this event is built (see
/// <see cref="DraftPayloadEndpoint"/>), so the immutable event store can never receive a credential.
/// </summary>
/// <param name="Name">The payload's logical name (from its <c>name</c> field).</param>
/// <param name="Script">The saved payload JSON (scrubbed; the reconstructable version history).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of <see cref="Script"/> — pins the exact stored bytes (drift/audit, same convention as <c>RunStarted</c>).</param>
/// <param name="DraftedAt">When the payload was drafted (through the <see cref="TimeProvider"/> seam).</param>
/// <param name="By">The actor who drafted it — stamped from the authenticated tenant's identity, never a request body (§12).</param>
public sealed record PayloadDrafted(string Name, string Script, string ScriptHash, DateTimeOffset DraftedAt, string By);

/// <summary>
/// A managed payload gained a new script revision (§14.1) — one event = one version. Carries the same scrubbed-then-hashed
/// <see cref="Script"/>/<see cref="ScriptHash"/> as a draft (a persisted revision is always executable, §12), plus an
/// optional <see cref="Note"/> for the audit trail. The revision number is the event's stream version (folded by the
/// aggregate), so it is not duplicated here. The actor (<see cref="By"/>) comes from the authenticated principal, never
/// the request body (§12).
/// </summary>
/// <param name="Script">The revised payload JSON (scrubbed).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of <see cref="Script"/>.</param>
/// <param name="Note">An optional human note describing the revision (scrubbed).</param>
/// <param name="RevisedAt">When the revision was saved (through the <see cref="TimeProvider"/> seam).</param>
/// <param name="By">The actor who revised it — stamped from the authenticated tenant's identity (§12).</param>
public sealed record PayloadRevised(string Script, string ScriptHash, string? Note, DateTimeOffset RevisedAt, string By);

/// <summary>
/// A managed payload was renamed (§14.1): metadata only, the script is unchanged. Advances the head revision but leaves
/// the script hash the same. The actor (<see cref="By"/>) comes from the authenticated principal (§12, as above).
/// </summary>
/// <param name="Name">The new logical name (scrubbed).</param>
/// <param name="RenamedAt">When the rename was saved (through the <see cref="TimeProvider"/> seam).</param>
/// <param name="By">The actor who renamed it — stamped from the authenticated tenant's identity (§12).</param>
public sealed record PayloadRenamed(string Name, DateTimeOffset RenamedAt, string By);

/// <summary>
/// A managed payload was archived (§14.1): a terminal lifecycle change. An archived payload cannot be revised, renamed,
/// re-archived, or pinned by a new run. The actor (<see cref="By"/>) comes from the authenticated principal (§12, as above).
/// </summary>
/// <param name="ArchivedAt">When the archive was saved (through the <see cref="TimeProvider"/> seam).</param>
/// <param name="By">The actor who archived it — stamped from the authenticated tenant's identity (§12).</param>
public sealed record PayloadArchived(DateTimeOffset ArchivedAt, string By);
