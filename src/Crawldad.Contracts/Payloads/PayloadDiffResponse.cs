using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Payloads;

/// <summary>How a single JSON location changed between two payload revisions (§14.1). Serialized camelCase via <see cref="ContractsJson"/>.</summary>
public enum PayloadDiffKind
{
    /// <summary>The location exists only in the <c>to</c> revision.</summary>
    Added,

    /// <summary>The location exists only in the <c>from</c> revision.</summary>
    Removed,

    /// <summary>The location exists in both revisions with a different value (or a different JSON kind).</summary>
    Changed,
}

/// <summary>
/// One structural change between two payload revisions: a JSON-Pointer <see cref="Path"/> into the payload documents, the
/// change <see cref="Kind"/>, and the before/after values. <see cref="From"/> is absent for an <see cref="PayloadDiffKind.Added"/>
/// change; <see cref="To"/> is absent for a <see cref="PayloadDiffKind.Removed"/> change; both are present for a
/// <see cref="PayloadDiffKind.Changed"/> change.
/// </summary>
/// <param name="Path">JSON Pointer to the changed location (empty for the document root).</param>
/// <param name="Kind">The kind of change.</param>
/// <param name="From">The value in the <c>from</c> revision (absent when added).</param>
/// <param name="To">The value in the <c>to</c> revision (absent when removed).</param>
public sealed record PayloadDiffEntry(
    string Path,
    PayloadDiffKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? From,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? To);

/// <summary>
/// The <c>GET /payloads/{id}/diff/{from}/{to}</c> response (§14.1): both revisions' scripts plus a minimal structural
/// diff — the set of JSON locations that changed, deepest-point only (an unchanged subtree yields no entries). Both
/// scripts are the stored, credential-scrubbed documents (§12).
/// </summary>
/// <param name="PayloadId">The payload's event-stream id.</param>
/// <param name="FromRevision">The base revision.</param>
/// <param name="ToRevision">The compared revision.</param>
/// <param name="FromScript">The base revision's payload document.</param>
/// <param name="ToScript">The compared revision's payload document.</param>
/// <param name="Changes">The structural changes (empty when the two scripts are identical).</param>
public sealed record PayloadDiffResponse(
    Guid PayloadId,
    int FromRevision,
    int ToRevision,
    JsonElement FromScript,
    JsonElement ToScript,
    IReadOnlyList<PayloadDiffEntry> Changes);
