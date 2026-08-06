namespace Crawldad.Contracts.Payloads;

/// <summary>The lifecycle state of a managed payload (§14.1). Serialized camelCase via <see cref="ContractsJson"/>.
/// A freshly drafted payload is <see cref="Active"/>; <see cref="Archived"/> arrives with the archive command (Phase 5).</summary>
public enum PayloadStatus
{
    /// <summary>The payload is live and runnable.</summary>
    Active,

    /// <summary>The payload has been archived (Phase 5).</summary>
    Archived,
}

/// <summary>
/// The <c>POST /payloads</c> success response (§10/§14.1): the persisted payload's identity and pinned head. A DTO of
/// the <c>Payload</c> aggregate state — never the internal aggregate itself (§14.1).
/// </summary>
/// <param name="PayloadId">The payload's event-stream id.</param>
/// <param name="Name">The payload's logical name (from its <c>name</c> field).</param>
/// <param name="Revision">The head revision — 1 for a freshly drafted payload (§14.1).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of the payload JSON — pins exactly what was saved (drift/audit, same convention as <c>RunStarted</c>).</param>
/// <param name="Status">The lifecycle state.</param>
public sealed record PayloadResponse(Guid PayloadId, string Name, int Revision, string ScriptHash, PayloadStatus Status);
