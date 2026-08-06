namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// A payload was drafted — revision 1 of a managed payload (§14.1). Each revision is one event, so the stream is the
/// version history. The full <see cref="Script"/> is stored inline (v1; large scripts would content-address the body
/// under <see cref="ScriptHash"/>, §14.1). A payload is automation data, not credentials — those are run-time
/// inputs-by-reference (§12), never part of the saved document — so the script is safe to persist verbatim.
/// </summary>
/// <param name="Name">The payload's logical name (from its <c>name</c> field).</param>
/// <param name="Script">The saved payload JSON verbatim (the reconstructable version history).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of <see cref="Script"/> — pins the exact bytes (drift/audit, same convention as <c>RunStarted</c>).</param>
/// <param name="DraftedAt">When the payload was drafted (through the <see cref="TimeProvider"/> seam).</param>
public sealed record PayloadDrafted(string Name, string Script, string ScriptHash, DateTimeOffset DraftedAt);
