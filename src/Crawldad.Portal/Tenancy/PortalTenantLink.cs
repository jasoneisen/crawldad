namespace Crawldad.Portal.Tenancy;

/// <summary>Links a portal account to the Crawldad tenant it acts as. Stored as a Marten document in the "portal"
/// schema, keyed by the account's normalized email — the SAME identity the OTP auth uses (<see
/// cref="Crawldad.Portal.Auth.PortalAuthService.NormalizeEmail"/>), so a link lines up one-to-one with its
/// <see cref="Crawldad.Portal.Auth.PortalUser"/>. The tenant's Crawldad API key lives ONLY as
/// <see cref="ProtectedApiKey"/> — ASP.NET Data-Protection ciphertext bound to a fixed purpose; the raw key is
/// never stored, logged, evented, or rendered to the browser. There is no UI to create links in this slice (the
/// account area owns that later); dev seeding and the future account UI both write through
/// <see cref="IPortalTenantLinkStore"/>, which protects the key at rest.</summary>
public sealed class PortalTenantLink
{
    /// <summary>The linked account's email — the document id, always normalized to lower-invariant (matching the
    /// <see cref="Crawldad.Portal.Auth.PortalUser"/> identity). Unique.</summary>
    public string Email { get; set; } = "";

    /// <summary>The Crawldad tenant this account acts as. Sent to the API implicitly via the tenant's API key; also
    /// surfaced for display in the account area.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>The tenant's Crawldad API key as Data-Protection ciphertext (purpose
    /// <see cref="PortalTenancy.ApiKeyProtectorPurpose"/>). Never the raw key; decrypted only per request by
    /// <see cref="IPortalTenantContext"/> to authenticate the tenant's <c>CrawldadClient</c>.</summary>
    public string ProtectedApiKey { get; set; } = "";

    /// <summary>When the link was first created (preserved across updates).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the link was last written (advanced on every upsert).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
