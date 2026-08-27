namespace Crawldad.Api.Features.Webhooks;

/// <summary>A tenant's registered webhook endpoint, stored as a plain tenant-scoped Marten document (the shared
/// <c>AllDocumentsAreMultiTenanted</c> policy qualifies every row by tenant, so a name is unique per tenant and tenant
/// isolation holds by construction). The signing secret lives only as <see cref="ProtectedSecret"/> — Data-Protection
/// ciphertext, never plaintext, never logged or evented.</summary>
public sealed class WebhookEndpoint
{
    /// <summary>The registered name (the document id). Unique per tenant.</summary>
    public string Id { get; set; } = "";

    /// <summary>The delivery target URL (validated https, non-private at registration).</summary>
    public string Url { get; set; } = "";

    /// <summary>The signing secret as Data-Protection ciphertext. Never the raw secret; decrypted only to sign a delivery.</summary>
    public string ProtectedSecret { get; set; } = "";

    /// <summary>The subscribed event types. Empty means "all terminal-run events".</summary>
    public IReadOnlyList<string> Events { get; set; } = [];

    /// <summary>When the name was first registered (preserved across updates).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the registration was last written (advanced on every update).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
