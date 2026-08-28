using Crawldad.Contracts.Tenancy;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The console authorization authority (issue #119 PR4): a tenant ↔ verified-email ↔ role edge. It decides
/// <i>who may act as which tenant</i> on the trusted-subsystem console path, and the <c>ConsolePrincipal</c> handler
/// resolves it <b>before any tenant scope exists</b> — exactly the constraint that already makes <see cref="RegistryTenant"/>
/// and <see cref="TenantApiKey"/> single-tenanted, so this is single-tenanted too, in the <c>crawldad</c> schema, opened on
/// the default tenant via the shared <see cref="Marten.IDocumentStore"/>.
///
/// <para>Many-to-many by construction (multi-workspace): a user's active memberships <b>are</b> their workspaces, and a
/// workspace's active memberships are its members. The <see cref="Email"/> is stored normalized
/// (<see cref="Crawldad.Contracts.EmailAddress.Normalize"/>) — byte-identical to the portal's <c>PortalUser</c>/
/// <c>PortalTenantLink</c> ids and to the inbound console-user selector — so a lookup never misses on casing. Removal is
/// reversible (<see cref="RevokedAt"/>, mirroring <see cref="TenantApiKey.RevokedAt"/>); a tenant's last active
/// <see cref="MembershipRole.Owner"/> can never be revoked (the anti-orphan invariant, enforced in the store).</para></summary>
public sealed class TenantMembership
{
    /// <summary>The membership record id (document id) — the handle a revoke targets.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning tenant's <see cref="RegistryTenant.Id"/> (the GUID tenant id). Memberships only ever reference
    /// registry tenants (env-fallback tenants have no membership surface).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>The member's portal identity — a normalized email
    /// (<see cref="Crawldad.Contracts.EmailAddress.Normalize"/>). Never a credential.</summary>
    public string Email { get; set; } = "";

    /// <summary>The member's role in this workspace.</summary>
    public MembershipRole Role { get; set; }

    /// <summary>When the membership was recorded (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the membership was last written (UTC) — advanced on a role change or revoke.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the membership was revoked (UTC), or null while it is active. A revoked membership never authorizes a
    /// console request.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
