namespace Crawldad.Api.Features.Browsers;

/// <summary>A tenant's registered browser connect credential, stored as a plain tenant-scoped Marten document (the
/// shared <c>AllDocumentsAreMultiTenanted</c> policy qualifies every row by tenant, so a name is unique per tenant and
/// tenant isolation holds by construction). The secret lives only as <see cref="ProtectedSecret"/> — Data-Protection
/// ciphertext, never plaintext, never logged or evented.</summary>
public sealed class BrowserRegistration
{
    /// <summary>The registered name (the document id, and the <c>credentialRef</c> payloads reference). Unique per tenant.</summary>
    public string Id { get; set; } = "";

    /// <summary>The backend adapter this credential is for (<c>browserbase</c>/<c>browserless</c>).</summary>
    public string Adapter { get; set; } = "";

    /// <summary>How the secret is used at connect (<c>connectUrl</c>/<c>apiKey</c>). Metadata for the listing + shape validation.</summary>
    public string Mode { get; set; } = "";

    /// <summary>The credential as Data-Protection ciphertext. Never the raw secret; decrypted only at connect time.</summary>
    public string ProtectedSecret { get; set; } = "";

    /// <summary>Optional provider options metadata (region, projectId, …). Never carries the secret; surfaced in listings.</summary>
    public IReadOnlyDictionary<string, string>? Options { get; set; }

    /// <summary>When the name was first registered (preserved across updates).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the registration was last written (advanced on every update).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
