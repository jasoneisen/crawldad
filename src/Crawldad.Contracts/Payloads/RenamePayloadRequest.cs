namespace Crawldad.Contracts.Payloads;

/// <summary>
/// The <c>POST /payloads/{id}/rename</c> body and Wolverine command (§14.1): changes a managed payload's logical
/// <see cref="Name"/> without touching its script. A metadata revision — it advances the head revision but leaves the
/// script hash unchanged (a rename is visible as drift-with-equal-hashes, §14.1).
/// </summary>
/// <param name="Name">The new logical name (must be non-empty; guarded at the boundary).</param>
public sealed record RenamePayloadRequest(string Name);
