using Crawldad.Contracts.Tenancy;
using Marten;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The <b>lifetime</b> one-free-tenant-per-email marker (issue #119 PR7). Keyed by the caller's normalized email
/// (<see cref="Crawldad.Contracts.EmailAddress.Normalize"/>) as the document id, it records that this email has <b>ever</b>
/// provisioned a free workspace — and is <b>never removed</b>. It is deliberately independent of
/// <see cref="TenantMembership"/>: revoking or leaving the created workspace's membership must not reset the entitlement, so
/// the check is "has this email ever provisioned?", not "does it have an active membership?". Additional workspaces beyond
/// the free one are created on a paid plan (a later PR), never through this surface. Stored single-tenanted in the
/// <c>crawldad</c> schema (like the registry/membership/audit documents — it resolves before any tenant scope), so the
/// atomic create can run under a per-email advisory lock alongside the registry write.</summary>
public sealed class FreeTenantEntitlement
{
    /// <summary>The normalized email that provisioned a free workspace — the document id (unique by construction, so a
    /// second insert for the same email collides). Never a credential.</summary>
    public string Email { get; set; } = "";

    /// <summary>The free workspace's tenant id (<see cref="RegistryTenant.Id"/>) this email was granted.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>When the free workspace was provisioned (UTC). Set once; the marker is never rewritten.</summary>
    public DateTimeOffset ProvisionedAt { get; set; }
}

/// <summary>The outcome of a free-tenant provision — a value so the one-per-email-ever invariant is enforced <b>in the
/// store</b>, under a per-email advisory lock, and the endpoint maps it to HTTP.</summary>
public enum FreeProvisionOutcome
{
    /// <summary>The email had no prior free workspace: the tenant, the creator's Owner membership, and the lifetime marker
    /// were all created atomically. <c>201</c>.</summary>
    Provisioned,

    /// <summary>The email has <b>already</b> provisioned a free workspace (its lifetime marker exists) — refused, no second
    /// tenant created. The caller maps this to a <c>409</c>. <see cref="FreeProvisionResult.TenantId"/> carries the existing
    /// workspace so the portal can recover the link to it.</summary>
    AlreadyProvisioned,
}

/// <summary>The result of <see cref="IFreeTenantProvisioningStore.ProvisionAsync"/>: the <see cref="Outcome"/> and the
/// tenant id it concerns — the freshly created one (<see cref="FreeProvisionOutcome.Provisioned"/>) or the pre-existing one
/// (<see cref="FreeProvisionOutcome.AlreadyProvisioned"/>).</summary>
/// <param name="Outcome">Whether a workspace was created or one already existed.</param>
/// <param name="TenantId">The created or pre-existing free workspace's tenant id.</param>
public readonly record struct FreeProvisionResult(FreeProvisionOutcome Outcome, string TenantId);

/// <summary>The persistence seam over the self-serve free-tenant provision (issue #119 PR7): it creates the
/// <see cref="RegistryTenant"/>, the creator's Owner <see cref="TenantMembership"/>, and the lifetime
/// <see cref="FreeTenantEntitlement"/> marker <b>in one transaction under a per-email advisory lock</b>, so the
/// one-free-tenant-per-email-ever rule holds even under a concurrent double-submit. Split out from Marten (mirroring the
/// registry/membership stores) so the invariant is unit-testable against a fake and the Marten wiring is exercised end to
/// end. The <paramref name="email"/> is expected already normalized — the endpoint normalizes at the boundary.</summary>
public interface IFreeTenantProvisioningStore
{
    /// <summary>Atomically provisions <paramref name="tenant"/> (an already-built free-tier <see cref="RegistryTenant"/>)
    /// for <paramref name="email"/>: under a per-email advisory lock it checks the lifetime marker, and — only when none
    /// exists — stores the tenant, an Owner membership for the email, and the marker, then commits. When the marker already
    /// exists nothing is written and the pre-existing tenant id is returned. Two concurrent calls for the same email queue
    /// on the lock, so exactly one creates and the other sees <see cref="FreeProvisionOutcome.AlreadyProvisioned"/>.</summary>
    Task<FreeProvisionResult> ProvisionAsync(string email, RegistryTenant tenant, DateTimeOffset now, CancellationToken ct);

    /// <summary>The email's lifetime free-tenant marker, or null when it has never provisioned one — for the tests and any
    /// read-side "already claimed?" check.</summary>
    Task<FreeTenantEntitlement?> FindMarkerAsync(string email, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="IFreeTenantProvisioningStore"/>. The provision documents are single-tenanted (the
/// registry + marker define/gate tenant scopes), so every session is opened on the default tenant via the shared
/// <see cref="IDocumentStore"/> — the same singleton-store, session-per-call shape as <see cref="MartenTenantRegistryStore"/>.</summary>
internal sealed class MartenFreeTenantProvisioningStore(IDocumentStore store) : IFreeTenantProvisioningStore
{
    public async Task<FreeProvisionResult> ProvisionAsync(string email, RegistryTenant tenant, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await using var session = store.LightweightSession();

        // Serialize this email's provisions so the check-then-create is atomic: two concurrent double-submits queue on the
        // per-EMAIL advisory lock (a distinct lock class from the tenant-scoped guards), and the second re-reads the marker
        // only after the first commits — so it sees the marker and refuses, never a second tenant. The unique document id
        // (the email) is the DB-level backstop, but the lock makes the refusal deterministic rather than a duplicate-key throw.
        await TenantWriteLock.AcquireAsync(session, TenantWriteLock.FreeProvisionClass, email, ct);

        var existing = await session.LoadAsync<FreeTenantEntitlement>(email, ct);
        if (existing is not null)
        {
            return new FreeProvisionResult(FreeProvisionOutcome.AlreadyProvisioned, existing.TenantId); // one free tenant per email, ever
        }

        // One transaction: the tenant, the creator's Owner membership (so a later console read for this email resolves to
        // the new workspace), and the lifetime marker — so a crash can never leave a tenant with no marker (which would let
        // the email re-provision) or a marker with no tenant.
        var membership = new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = email,
            Role = MembershipRole.Owner,
            CreatedAt = now,
            UpdatedAt = now,
        };
        session.Store(tenant);
        session.Store(membership);
        session.Store(new FreeTenantEntitlement { Email = email, TenantId = tenant.Id, ProvisionedAt = now });
        await session.SaveChangesAsync(ct);
        return new FreeProvisionResult(FreeProvisionOutcome.Provisioned, tenant.Id);
    }

    public async Task<FreeTenantEntitlement?> FindMarkerAsync(string email, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<FreeTenantEntitlement>(email, ct);
    }
}
