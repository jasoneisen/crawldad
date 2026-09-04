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

/// <summary>The outcome of a membership role change (issue #119 PR6) — a value so the last-Owner invariant is enforced <b>in
/// the store</b>, under the tenant advisory lock, and the endpoint maps it to HTTP.</summary>
public enum MembershipRoleChangeOutcome
{
    /// <summary>The membership now carries the requested role (or already did — an idempotent no-op).</summary>
    Changed,

    /// <summary>No such active membership for the tenant (unknown id, another tenant's, or revoked) — a plain not-found.</summary>
    NotFound,

    /// <summary>Refused: the change would downgrade the tenant's <b>last active <see cref="MembershipRole.Owner"/></b> to a
    /// <see cref="MembershipRole.Member"/>, orphaning the workspace. The caller maps this to a <c>409</c>.</summary>
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
    /// idempotently. Convenience over <see cref="CreateAsync"/> for the self-service attach flow's self-owner write.</summary>
    Task<TenantMembership> CreateOwnerAsync(string tenantId, string email, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records an active membership for <paramref name="email"/> in the tenant with <paramref name="role"/>,
    /// idempotently: if an active membership already exists it is returned unchanged — <b>no duplicate, and its role is not
    /// altered</b> (a role change is <see cref="ChangeRoleAsync"/>); else a new membership is created. The active
    /// <c>(tenant, email)</c> uniqueness is enforced atomically under the tenant advisory lock (issue #119 PR6), so two
    /// concurrent attaches of the same pair can never both insert.</summary>
    Task<TenantMembership> CreateAsync(string tenantId, string email, MembershipRole role, DateTimeOffset now, CancellationToken ct);

    /// <summary>Every membership for the tenant (active and revoked), newest first — for the member listing and the
    /// last-owner invariant.</summary>
    Task<IReadOnlyList<TenantMembership>> ListForTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>Every active membership for <paramref name="email"/> across tenants (the user's workspaces), newest first.</summary>
    Task<IReadOnlyList<TenantMembership>> ListForEmailAsync(string email, CancellationToken ct);

    /// <summary>True when the tenant has at least one active <see cref="MembershipRole.Owner"/> membership — the human
    /// OTP→console recovery path that makes revoking the tenant's last API key safe (issue #119 PR5). A tenant's active
    /// memberships are a handful of rows, so the implementation reads that set and folds the Owner count in memory rather
    /// than pushing a second predicate down.</summary>
    Task<bool> HasActiveOwnerAsync(string tenantId, CancellationToken ct);

    /// <summary>Revokes the tenant's active membership <paramref name="membershipId"/>, stamping <paramref name="now"/> —
    /// unless it is the tenant's last active Owner, which is refused (<see cref="MembershipRevokeOutcome.LastOwner"/>) so the
    /// workspace is never orphaned. Idempotent: a repeat revoke is <see cref="MembershipRevokeOutcome.NotFound"/>. The
    /// remove-member write; self-removal is allowed (any non-last-Owner membership is removable).</summary>
    Task<MembershipRevokeOutcome> RevokeAsync(string tenantId, Guid membershipId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Sets the tenant's active membership <paramref name="membershipId"/> to <paramref name="newRole"/>, stamping
    /// <paramref name="now"/> — unless it would downgrade the tenant's last active Owner to a Member, which is refused
    /// (<see cref="MembershipRoleChangeOutcome.LastOwner"/>) so the workspace keeps an Owner. Setting the role a membership
    /// already has is an idempotent <see cref="MembershipRoleChangeOutcome.Changed"/>. Enforced atomically under the tenant
    /// advisory lock (the cross-row Owner count the document version cannot cover). Issue #119 PR6.</summary>
    Task<MembershipRoleChangeOutcome> ChangeRoleAsync(string tenantId, Guid membershipId, MembershipRole newRole, DateTimeOffset now, CancellationToken ct);
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

    public Task<TenantMembership> CreateOwnerAsync(string tenantId, string email, DateTimeOffset now, CancellationToken ct) =>
        CreateAsync(tenantId, email, MembershipRole.Owner, now, ct);

    public async Task<TenantMembership> CreateAsync(string tenantId, string email, MembershipRole role, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();

        // Serialize this tenant's membership writes so the check-then-insert is atomic (issue #119 PR6, PR#154 forward item):
        // two concurrent attaches of the same (tenant, email) queue on the lock, and the second re-reads only after the first
        // commits — so it sees the existing row and returns it, never a duplicate. The partial unique index on active
        // (TenantId, Email) (ManagementModule) is the DB-level backstop. Same lock class as revoke/role-change: all of a
        // tenant's membership mutations serialise, so a create racing a revoke-of-last-owner is well-ordered too.
        await TenantWriteLock.AcquireAsync(session, TenantWriteLock.MembershipRevocationClass, tenantId, ct);

        // An existing active membership is returned unchanged so a re-attach is a clean no-op — and its role is NOT altered
        // here (a role change is ChangeRoleAsync), so a second record can never silently promote/demote a member.
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
            Role = role,
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

    public async Task<bool> HasActiveOwnerAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var active = await session.Query<TenantMembership>()
            .Where(m => m.TenantId == tenantId && m.RevokedAt == null)
            .ToListAsync(ct);
        return active.Any(m => m.Role == MembershipRole.Owner); // a handful of rows per tenant — folding Owner in memory is free
    }

    public async Task<MembershipRevokeOutcome> RevokeAsync(string tenantId, Guid membershipId, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();

        // Serialize this tenant's membership revokes so the last-Owner guard is atomic (issue #119 PR5): a concurrent revoke
        // of a sibling Owner queues on the lock and re-reads the owner count only after this one commits — two racing revokes
        // can never both pass the guard and orphan the workspace. TenantMembership also carries optimistic concurrency, so a
        // stale write to the SAME membership loses; the lock covers the cross-row count the version cannot.
        await TenantWriteLock.AcquireAsync(session, TenantWriteLock.MembershipRevocationClass, tenantId, ct);

        var membership = await session.LoadAsync<TenantMembership>(membershipId, ct);
        if (membership is null || !string.Equals(membership.TenantId, tenantId, StringComparison.Ordinal) || membership.RevokedAt is not null)
        {
            return MembershipRevokeOutcome.NotFound; // unknown, another tenant's, or already revoked — idempotent no-op
        }

        // Anti-orphan invariant: never remove the tenant's last active Owner. A tenant's active memberships are a handful of
        // rows, so the guard reads that set once and folds the Owner count in memory — the count includes the membership being
        // revoked, which is what makes <= 1 the right test.
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

    public async Task<MembershipRoleChangeOutcome> ChangeRoleAsync(string tenantId, Guid membershipId, MembershipRole newRole, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();

        // Same lock as revoke: the downgrade guard below re-reads the Owner count, which must be atomic against a concurrent
        // revoke/downgrade of a sibling Owner — two races can never both strip the workspace of its last Owner.
        await TenantWriteLock.AcquireAsync(session, TenantWriteLock.MembershipRevocationClass, tenantId, ct);

        var membership = await session.LoadAsync<TenantMembership>(membershipId, ct);
        if (membership is null || !string.Equals(membership.TenantId, tenantId, StringComparison.Ordinal) || membership.RevokedAt is not null)
        {
            return MembershipRoleChangeOutcome.NotFound; // unknown, another tenant's, or revoked — idempotent not-found
        }

        if (membership.Role == newRole)
        {
            return MembershipRoleChangeOutcome.Changed; // already at the requested role — idempotent no-op, no write
        }

        // Anti-orphan invariant: a downgrade from Owner to Member removes an active Owner — refuse if it is the last one.
        // Same shape as the revoke guard: one read of the tenant's (few) active memberships, Owner count folded in memory —
        // the count includes the membership being downgraded, hence <= 1.
        if (membership.Role == MembershipRole.Owner && newRole == MembershipRole.Member)
        {
            var activeOwners = await session.Query<TenantMembership>()
                .Where(m => m.TenantId == tenantId && m.RevokedAt == null)
                .ToListAsync(ct);
            if (activeOwners.Count(m => m.Role == MembershipRole.Owner) <= 1)
            {
                return MembershipRoleChangeOutcome.LastOwner;
            }
        }

        membership.Role = newRole;
        membership.UpdatedAt = now;
        session.Store(membership);
        await session.SaveChangesAsync(ct);
        return MembershipRoleChangeOutcome.Changed;
    }
}
