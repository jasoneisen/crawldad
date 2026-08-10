using System.Text.Json;

namespace Crawldad.Contracts.Payloads;

/// <summary>One historical revision of a managed payload, reconstructed from the event stream. <see cref="Script"/> is
/// the stored, credential-scrubbed document — exactly the bytes a run pinned at this revision executes.</summary>
public sealed record PayloadRevisionResponse(Guid PayloadId, int Revision, string ScriptHash, JsonElement Script);
