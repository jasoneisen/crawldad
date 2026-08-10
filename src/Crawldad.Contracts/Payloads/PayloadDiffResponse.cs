using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Payloads;

/// <summary>How a single JSON location changed between two payload revisions. Serialized camelCase via <see cref="ContractsJson"/>.</summary>
public enum PayloadDiffKind
{
    /// <summary>The location exists only in the <c>to</c> revision.</summary>
    Added,

    /// <summary>The location exists only in the <c>from</c> revision.</summary>
    Removed,

    /// <summary>The location exists in both revisions with a different value (or a different JSON kind).</summary>
    Changed,
}

/// <summary>One structural change: a JSON-Pointer <see cref="Path"/>, the <see cref="Kind"/>, and before/after values —
/// <see cref="From"/> absent when added, <see cref="To"/> absent when removed, both present when changed.</summary>
public sealed record PayloadDiffEntry(
    string Path,
    PayloadDiffKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? From,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? To);

/// <summary>The diff response: both revisions' scripts plus the structural diff, deepest-point only (an unchanged
/// subtree yields no entries); both scripts are the stored, credential-scrubbed documents.</summary>
public sealed record PayloadDiffResponse(
    Guid PayloadId,
    int FromRevision,
    int ToRevision,
    JsonElement FromScript,
    JsonElement ToScript,
    IReadOnlyList<PayloadDiffEntry> Changes);
