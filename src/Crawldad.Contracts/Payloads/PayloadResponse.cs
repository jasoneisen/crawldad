namespace Crawldad.Contracts.Payloads;

/// <summary>The lifecycle state of a managed payload. Serialized camelCase via <see cref="ContractsJson"/>.
/// A freshly drafted payload is <see cref="Active"/>; <see cref="Archived"/> arrives with the archive command.
/// <para><b>Stored as its ordinal.</b> The wire is camelCase names, but the store is not: with no Marten serializer
/// override the default <c>EnumStorage.AsInteger</c> is in force, so the integers below — not the names — are what sits
/// in the <c>Payload</c> snapshot and the <c>PayloadSummary</c> projection view. The explicit values are an append-only
/// contract: add a member with the next free value, <b>never renumber</b>, and retire one as a tombstone that keeps its
/// value. Pinned member-by-member in <c>EnumOrdinalContractTests</c>.</para></summary>
public enum PayloadStatus
{
    /// <summary>The payload is live and runnable.</summary>
    Active = 0,

    /// <summary>The payload has been archived.</summary>
    Archived = 1,
}

/// <summary>The <c>POST /payloads</c> success response: the persisted payload's identity and pinned head — a DTO of
/// the <c>Payload</c> aggregate state, never the internal aggregate itself.</summary>
public sealed record PayloadResponse(Guid PayloadId, string Name, int Revision, string ScriptHash, PayloadStatus Status);
