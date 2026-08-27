namespace Crawldad.Api.Infrastructure.Security;

/// <summary>A registry tenant's lifecycle state. A <see cref="Suspended"/> tenant's keys still exist but no longer
/// authenticate — a suspended tenant is rejected at the auth boundary exactly like an unknown key (no existence oracle).</summary>
public enum TenantStatus
{
    /// <summary>The tenant is live: its non-revoked keys authenticate and its runs are admitted.</summary>
    Active,

    /// <summary>The tenant is suspended: every key is rejected at auth until the tenant is reactivated.</summary>
    Suspended,
}

/// <summary>The DB-backed, registry-owned tenant record — the durable counterpart to a <see cref="TenantDescriptor"/>
/// configured in <c>Crawldad:Tenants</c>. Stored <b>single-tenanted</b> (it defines tenants, so it cannot itself be
/// scoped by one), it is the billing subject and the Marten conjoined partition id every authenticated request scopes
/// to. Registry lookups fall back to the env-configured tenants, so existing wiring keeps working unchanged.</summary>
public sealed class RegistryTenant
{
    /// <summary>The stable tenant id — the document id, the Marten tenant partition key, and the billing subject. A
    /// lowercase slug (no <c>':'</c>, which would make the per-tenant secret-vault namespace ambiguous — the same
    /// constraint <see cref="TenantRegistry"/> enforces on a configured tenant id).</summary>
    public string Id { get; set; } = "";

    /// <summary>The human-facing display name (portal/label only; never load-bearing for identity or scoping).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The actor/display identity stamped on this tenant's mutation events — issued into the auth principal,
    /// never taken from a request body (parity with <see cref="TenantDescriptor.Actor"/>).</summary>
    public string Actor { get; set; } = "";

    /// <summary>The tenant's lifecycle state. A suspended tenant is rejected at the auth boundary.</summary>
    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>The plan tier moniker (e.g. <c>free</c>/<c>pro</c>). A minimal, free-form label; the enforced knob is
    /// <see cref="SlotAllowance"/>.</summary>
    public string Tier { get; set; } = "";

    /// <summary>The per-tenant concurrent-run allowance — the registry counterpart of
    /// <see cref="TenantDescriptor.MaxConcurrentRuns"/>. When set it overrides the global
    /// <see cref="Crawldad.Api.Features.Runs.RunLimitsOptions.MaxConcurrentRunsPerTenant"/> in the admission gate; null
    /// defers to the global default.</summary>
    public int? SlotAllowance { get; set; }

    /// <summary>When the tenant was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the tenant record was last written — advanced on a status change (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One issued API key for a <see cref="RegistryTenant"/>, stored <b>hashed</b>: only the SHA-256 of the full
/// high-entropy key (as lowercase hex) is persisted, never the raw key — which is returned exactly once at issue time.
/// A tenant may hold many keys (rotation); a key is active while <see cref="RevokedAt"/> is null. Stored single-tenanted
/// alongside <see cref="RegistryTenant"/> so the auth boundary can resolve it before any tenant scope is known.</summary>
public sealed class TenantApiKey
{
    /// <summary>The key record id (document id) — the handle a revoke targets.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning tenant's <see cref="RegistryTenant.Id"/>.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>The SHA-256 of the full raw key, lowercase hex. The only stored form of the secret; the presented key is
    /// hashed the same way and matched against this. High entropy makes the hash pre-image-safe without a salt.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>A short, non-secret display prefix (<c>ck_&lt;env&gt;_&lt;first-chars&gt;</c>) so a key is identifiable in
    /// a listing without revealing it. Safe to log and to show in the portal.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>When the key was issued (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the key was last successfully presented (UTC), best-effort: advanced on a cache-miss resolution
    /// (roughly once per cache TTL per key), not on every request, and never at the cost of failing the request.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>When the key was revoked (UTC), or null while it is active. A revoked key never authenticates.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
