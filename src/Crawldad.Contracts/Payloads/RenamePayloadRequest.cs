namespace Crawldad.Contracts.Payloads;

/// <summary>The <c>POST /payloads/{id}/rename</c> body and Wolverine command: changes a payload's <see cref="Name"/>
/// without touching its script — advances the head revision but keeps the script hash (drift-with-equal-hashes).</summary>
/// <param name="Name">Must be non-empty (guarded at the boundary).</param>
public sealed record RenamePayloadRequest(string Name);
