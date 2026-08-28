using Crawldad.Contracts.Tenancy;
using Marten;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The outcome of a membership revoke — modelled as a value so the anti-orphan invariant is enforced <b>in the
/// store</b> (not left to a caller), and a future member-management endpoint maps it to HTTP.</summary>
public enum MembershipRevokeOutcome
{
    /// <summary>The membership was active and is now revoked.</summary>
    Revoked,

    /// <summary>No such active membership for the tenant (unknown id, another tenant's, or already revoked) — a plain
    /// not-found, no existence oracle.</summary>
    NotFound,

    /// <summary>Refused: the membership is the tenant's <b>last active <see cref="MembershipRole.Owner"/></b>. Removing (or
    /// downgrading) it would orphan the workspace, so it is rejected — the caller maps this to a <c>409</c>. The workspace
    /// must always retain at least one Owner as its human recovery path.</summary>
    LastOwner,
}

/// <summary>The persistence seam over the console <see cref="TenantMembership"/> documents, stored single-tenanted so the
/// auth boundary can resolve them before any tenant scope exists. Split out from Marten (mirroring
/// <see cref="ITenantRegistryStore"/>) so the invariant logic is unit-testable against the store and the Marten wiring is
/// exercised end-to-end. Every email argument is expected already normalized
/// (<see cref="Crawldad.Contracts.EmailAddress.Normalize"/>) — callers normalize at the boundary.</summary>
public interface ITenantMembershipStore
{
    /// <summary>The console auth hot path: the tenant's active membership for <paramref name="email"/>, or null when the
    /// user is not an active member (→ the console request is <c>403</c>).</summary>
    Task<TenantMembership?> FindActiveAsync(string tenantId, string email, CancellationToken ct);

    /// <summary>Records an active <see cref="MembershipRole.Owner"/> membership for <paramref name="email"/> in the tenant,
    /// idempotently: if an active membership already exists it is returned unchanged (no duplicate), else a new Owner
    /// membership is created. The self-service attach flow's write.</summary>
    Task<TenantMembership> CreateOwnerAsync(string tenantId, string email, DateTimeOffset now, CancellationToken ct);

    /// <summary>Every membership for the tenant (active and revoked), newest first — for the member listing and the
    /// last-owner invariant.</summary>
    Task<IReadOnlyList<TenantMembership>> ListForTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>Every active membership for <paramref name="email"/> across tenants (the user's workspaces), newest first.</summary>
    Task<IReadOnlyList<TenantMembership>> ListForEmailAsync(string email, CancellationToken ct);

    /// <summary>Revokes the tenant's active membership <paramref name="membershipId"/>, stamping <paramref name="now"/> —
    /// unless it is the tenant's last active Owner, which is refused (<see cref="MembershipRevokeOutcome.LastOwner"/>) so the
    /// workspace is never orphaned. Idempotent: a repeat revoke is <see cref="MembershipRevokeOutcome.NotFound"/>.</summary>
    Task<MembershipRevokeOutcome> RevokeAsync(string tenantId, Guid membershipId, DateTimeOffset now, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="ITenantMembershipStore"/>. The membership documents are single-tenanted (they
/// define who may become a tenant scope), so every session is opened on the default tenant via the shared
/// <see cref="IDocumentStore"/> — the same singleton-store, session-per-call shape as <see cref="MartenTenantRegistryStore"/>.</summary>
internal sealed class MartenTenantMembershipStore(IDocumentStore store) : ITenantMembershipStore
{
    public async Task<TenantMembership?> FindActiveAsync(string tenantId, string email, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return await session.Query<TenantMembership>()
            .Where(m => m.TenantId == tenantId && m.Email == email && m.RevokedAt == null)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<TenantMembership> CreateOwnerAsync(string tenantId, string email, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();

        // Active-uniqueness is enforced here (check-then-insert, the MartenTenantRegistryStore.CreateAsync shape): an
        // existing active membership is returned unchanged so a re-attach is a clean no-op, never a duplicate.
        var existing = await session.Query<TenantMembership>()
            .Where(m => m.TenantId == tenantId && m.Email == email && m.RevokedAt == null)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var membership = new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = email,
            Role = MembershipRole.Owner,
            CreatedAt = now,
            UpdatedAt = now,
        };
        session.Store(membership);
        await session.SaveChangesAsync(ct);
        return membership;
    }

    public async Task<IReadOnlyList<TenantMembership>> ListForTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return await session.Query<TenantMembership>()
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TenantMembership>> ListForEmailAsync(string email, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return await session.Query<TenantMembership>()
            .Where(m => m.Email == email && m.RevokedAt == null)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<MembershipRevokeOutcome> RevokeAsync(string tenantId, Guid membershipId, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var membership = await session.LoadAsync<TenantMembership>(membershipId, ct);
        if (membership is null || !string.Equals(membership.TenantId, tenantId, StringComparison.Ordinal) || membership.RevokedAt is not null)
        {
            return MembershipRevokeOutcome.NotFound; // unknown, another tenant's, or already revoked — idempotent no-op
        }

        // Anti-orphan invariant: never remove the tenant's last active Owner. Count owners in memory (membership sets are
        // small) so the guard never depends on enum-in-LINQ translation.
        if (membership.Role == MembershipRole.Owner)
        {
            var activeOwners = await session.Query<TenantMembership>()
                .Where(m => m.TenantId == tenantId && m.RevokedAt == null)
                .ToListAsync(ct);
            if (activeOwners.Count(m => m.Role == MembershipRole.Owner) <= 1)
            {
                return MembershipRevokeOutcome.LastOwner;
            }
        }

        membership.RevokedAt = now;
        membership.UpdatedAt = now;
        session.Store(membership);
        await session.SaveChangesAsync(ct);
        return MembershipRevokeOutcome.Revoked;
    }
}
