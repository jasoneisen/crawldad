namespace Crawldad.Web.Features.Payloads;

/// <summary>A payload was drafted — revision 1 of a managed payload; each revision is one event, so the stream is the
/// version history. Payloads are automation data, not credentials, but the script is nonetheless credential-scrubbed at
/// the persistence boundary as defence in depth, so the immutable event store can never receive a credential.</summary>
public sealed record PayloadDrafted(string Name, string Script, string ScriptHash, DateTimeOffset DraftedAt, string By);

/// <summary>A managed payload gained a new script revision — one event = one version. The revision number is the event's
/// stream version (folded by the aggregate), so it is not duplicated here. Carries the same scrubbed-then-hashed
/// <see cref="Script"/>/<see cref="ScriptHash"/> as a draft; the actor comes from the authenticated principal.</summary>
public sealed record PayloadRevised(string Script, string ScriptHash, string? Note, DateTimeOffset RevisedAt, string By);

/// <summary>A managed payload was renamed: metadata only, the script is unchanged. Advances the head revision but leaves
/// the script hash the same. The actor comes from the authenticated principal.</summary>
public sealed record PayloadRenamed(string Name, DateTimeOffset RenamedAt, string By);

/// <summary>A managed payload was archived: a terminal lifecycle change. An archived payload cannot be revised, renamed,
/// re-archived, or pinned by a new run. The actor comes from the authenticated principal.</summary>
public sealed record PayloadArchived(DateTimeOffset ArchivedAt, string By);
