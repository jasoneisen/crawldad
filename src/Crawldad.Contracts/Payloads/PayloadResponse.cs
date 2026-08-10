namespace Crawldad.Contracts.Payloads;

/// <summary>The lifecycle state of a managed payload. Serialized camelCase via <see cref="ContractsJson"/>.
/// A freshly drafted payload is <see cref="Active"/>; <see cref="Archived"/> arrives with the archive command.</summary>
public enum PayloadStatus
{
    /// <summary>The payload is live and runnable.</summary>
    Active,

    /// <summary>The payload has been archived.</summary>
    Archived,
}

/// <summary>The <c>POST /payloads</c> success response: the persisted payload's identity and pinned head — a DTO of
/// the <c>Payload</c> aggregate state, never the internal aggregate itself.</summary>
public sealed record PayloadResponse(Guid PayloadId, string Name, int Revision, string ScriptHash, PayloadStatus Status);
