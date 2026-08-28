using System.Runtime.InteropServices;
using Marten;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>A flattened, secret-free view of a tenant — everything the auth boundary and the admission gate need, without
/// touching the document. Carried by the auth cache (so a cache hit answers without a query) and by a key resolution.</summary>
/// <param name="Id">The tenant partition id.</param>
/// <param name="Actor">The actor stamped on the tenant's mutation events.</param>
/// <param name="Status">The tenant's lifecycle state (a suspended tenant is rejected at auth).</param>
/// <param name="SlotAllowance">The per-tenant concurrent-run override, or null to defer to the global default.</param>
public readonly record struct RegistryTenantSnapshot(string Id, string Actor, TenantStatus Status, int? SlotAllowance);

/// <summary>A presented key resolved to its owning tenant: the matched key's id (for the best-effort last-used touch) and
/// the tenant snapshot. Returned regardless of tenant status — the caller enforces suspension.</summary>
/// <param name="KeyId">The matched <see cref="TenantApiKey.Id"/>.</param>
/// <param name="Tenant">The owning tenant's snapshot.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ResolvedTenantKey(Guid KeyId, RegistryTenantSnapshot Tenant);

/// <summary>The outcome of a guarded key revoke (issue #119 PR5) — modelled as a value so the last-active-key invariant is
/// enforced <b>in the store</b>, under a tenant advisory lock, and the endpoint maps it to HTTP.</summary>
public enum KeyRevokeOutcome
{
    /// <summary>The key was active and is now revoked.</summary>
    Revoked,

    /// <summary>No such active key for the tenant (unknown id, another tenant's, or already revoked) — a plain not-found.</summary>
    NotFound,

    /// <summary>Refused: revoking would leave the tenant with <b>no active key</b>, and no console recovery path exists (the
    /// caller passed <c>allowLastActive: false</c>). The caller maps this to a <c>409</c>.</summary>
    LastActive,

    /// <summary>Refused: the key being revoked is the one authenticating <b>this</b> request; revoking it would break the
    /// caller mid-session. Rotate it instead. The caller maps this to a <c>409</c>. (A console request presents no key, so
    /// this never fires there — which is what lets a console owner revoke the tenant's last key.)</summary>
    CurrentKey,
}

/// <summary>The persistence seam over the registry documents (<see cref="RegistryTenant"/> + <see cref="TenantApiKey"/>),
/// stored single-tenanted so the auth boundary can resolve them before any tenant scope exists. Split out from Marten so
/// the branchy directory/cache logic is unit-testable against a fake, and the Marten wiring is exercised end-to-end.</summary>
public interface ITenantRegistryStore
{
    /// <summary>Loads a tenant by id, or null when unknown.</summary>
    Task<RegistryTenant?> FindAsync(string tenantId, CancellationToken ct);

    /// <summary>Inserts a new tenant; false (without overwriting) when the id already exists.</summary>
    Task<bool> CreateAsync(RegistryTenant tenant, CancellationToken ct);

    /// <summary>Sets a tenant's status (suspend/reactivate), stamping <paramref name="now"/> as its updated time; returns
    /// the updated tenant, or null when the id is unknown. Idempotent — setting the current status is a no-op write.</summary>
    Task<RegistryTenant?> SetStatusAsync(string tenantId, TenantStatus status, DateTimeOffset now, CancellationToken ct);

    /// <summary>Sets a tenant's plan — its <see cref="RegistryTenant.Tier"/> moniker and <see cref="RegistryTenant.SlotAllowance"/>
    /// (null defers to the global default) — stamping <paramref name="now"/>. The write side of a verified billing webhook;
    /// returns the updated tenant, or null when the id is unknown (an unknown/env-fallback tenant is not written).</summary>
    Task<RegistryTenant?> SetPlanAsync(string tenantId, string tier, int? slotAllowance, DateTimeOffset now, CancellationToken ct);

    /// <summary>Persists a newly issued key (already hashed).</summary>
    Task AddKeyAsync(TenantApiKey key, CancellationToken ct);

    /// <summary>Revokes the tenant's active key <paramref name="keyId"/>, stamping <paramref name="now"/>; false when no
    /// such active key belongs to the tenant (unknown, foreign, or already revoked) — so a repeat revoke is idempotent. No
    /// last-active-key guard (the operator management surface may revoke any key); the self-service path uses
    /// <see cref="RevokeKeyGuardedAsync"/>.</summary>
    Task<bool> RevokeKeyAsync(string tenantId, Guid keyId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Revokes the tenant's active key <paramref name="keyId"/> under a tenant advisory lock, enforcing the
    /// last-active-key and current-key guards <b>atomically</b> (issue #119 PR5): it re-reads the active-key count under the
    /// lock, so two concurrent revokes can never both empty the tenant's active set. Precedence: an unknown/foreign/already
    /// -revoked id is <see cref="KeyRevokeOutcome.NotFound"/>; then, when <paramref name="allowLastActive"/> is false and this
    /// is the tenant's only active key, <see cref="KeyRevokeOutcome.LastActive"/> (rotate/console instead); then, when
    /// <paramref name="presentedKeyHash"/> matches the target (the key authenticating this request),
    /// <see cref="KeyRevokeOutcome.CurrentKey"/> (rotate instead) — so a key-authenticated caller can never revoke its own
    /// in-flight key even with a console recovery path, while a console caller (whose presented hash matches nothing) can
    /// revoke the tenant's last key. Otherwise it revokes and returns <see cref="KeyRevokeOutcome.Revoked"/>.</summary>
    Task<KeyRevokeOutcome> RevokeKeyGuardedAsync(string tenantId, Guid keyId, string presentedKeyHash, bool allowLastActive, DateTimeOffset now, CancellationToken ct);

    /// <summary>Atomically rotates a key: in one transaction, revokes the tenant's active key <paramref name="oldKeyId"/>
    /// (stamping <paramref name="now"/>) and persists <paramref name="replacement"/>, which inherits the rotated key's
    /// <see cref="TenantApiKey.Label"/>. Returns the stored replacement, or null when no such active key belongs to the
    /// tenant (unknown, foreign, or already revoked) — in which case <b>nothing is written</b>, so a freshly-minted
    /// replacement is discarded unused (its raw key never leaves the caller and was never persisted).</summary>
    Task<TenantApiKey?> RotateKeyAsync(string tenantId, Guid oldKeyId, TenantApiKey replacement, DateTimeOffset now, CancellationToken ct);

    /// <summary>Every key for the tenant (active and revoked), newest first, for a prefix-only listing.</summary>
    Task<IReadOnlyList<TenantApiKey>> ListKeysAsync(string tenantId, CancellationToken ct);

    /// <summary>The auth hot path: resolves a presented key hash to its non-revoked key + owning tenant snapshot, or null
    /// when no active key matches (or the key dangles with no tenant).</summary>
    Task<ResolvedTenantKey?> ResolveKeyAsync(string keyHash, CancellationToken ct);

    /// <summary>Best-effort advance of a key's last-used time; a failure here must never fail the authenticated request.</summary>
    Task TouchLastUsedAsync(Guid keyId, DateTimeOffset now, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="ITenantRegistryStore"/>. The registry documents are single-tenanted (they define
/// tenants), so every session is opened on the default tenant via the shared <see cref="IDocumentStore"/> — the same
/// singleton-store, open-a-session-per-call shape the browser/webhook/fixture stores use.</summary>
internal sealed class MartenTenantRegistryStore(IDocumentStore store) : ITenantRegistryStore
{
    public async Task<RegistryTenant?> FindAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<RegistryTenant>(tenantId, ct);
    }

    public async Task<bool> CreateAsync(RegistryTenant tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await using var session = store.LightweightSession();
        if (await session.LoadAsync<RegistryTenant>(tenant.Id, ct) is not null)
        {
            return false; // id already taken — never overwrite an existing tenant
        }

        session.Store(tenant);
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<RegistryTenant?> SetStatusAsync(string tenantId, TenantStatus status, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var tenant = await session.LoadAsync<RegistryTenant>(tenantId, ct);
        if (tenant is null)
        {
            return null;
        }

        tenant.Status = status;
        tenant.UpdatedAt = now;
        session.Store(tenant);
        await session.SaveChangesAsync(ct);
        return tenant;
    }

    public async Task<RegistryTenant?> SetPlanAsync(string tenantId, string tier, int? slotAllowance, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tier);
        await using var session = store.LightweightSession();
        var tenant = await session.LoadAsync<RegistryTenant>(tenantId, ct);
        if (tenant is null)
        {
            return null;
        }

        tenant.Tier = tier;
        tenant.SlotAllowance = slotAllowance;
        tenant.UpdatedAt = now;
        session.Store(tenant);
        await session.SaveChangesAsync(ct);
        return tenant;
    }

    public async Task AddKeyAsync(TenantApiKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var session = store.LightweightSession();
        session.Store(key);
        await session.SaveChangesAsync(ct);
    }

    public async Task<bool> RevokeKeyAsync(string tenantId, Guid keyId, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var key = await session.LoadAsync<TenantApiKey>(keyId, ct);
        if (key is null || !string.Equals(key.TenantId, tenantId, StringComparison.Ordinal) || key.RevokedAt is not null)
        {
            return false; // unknown, belongs to another tenant, or already revoked — idempotent no-op
        }

        key.RevokedAt = now;
        session.Store(key);
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<KeyRevokeOutcome> RevokeKeyGuardedAsync(string tenantId, Guid keyId, string presentedKeyHash, bool allowLastActive, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();

        // Serialize this tenant's revokes so the count-then-revoke below is atomic: a concurrent revoke of a sibling key
        // queues on the lock and re-reads the count only after this one commits — neither can empty the active set behind
        // the other's back. The lock releases when SaveChangesAsync commits (or the session disposes without a save).
        await TenantWriteLock.AcquireAsync(session, TenantWriteLock.KeyRevocationClass, tenantId, ct);

        var key = await session.LoadAsync<TenantApiKey>(keyId, ct);
        if (key is null || !string.Equals(key.TenantId, tenantId, StringComparison.Ordinal) || key.RevokedAt is not null)
        {
            return KeyRevokeOutcome.NotFound; // unknown, another tenant's, or already revoked — idempotent no-op
        }

        if (!allowLastActive)
        {
            // Under the lock, this count reflects every committed revoke — so the last-active guard cannot be raced. Checked
            // before the current-key guard so a tenant with no console recovery keeps its last-key refusal even for its own key.
            var active = await session.Query<TenantApiKey>().CountAsync(k => k.TenantId == tenantId && k.RevokedAt == null, ct);
            if (active <= 1)
            {
                return KeyRevokeOutcome.LastActive; // the tenant's only live key, and no console recovery — refuse (409)
            }
        }

        if (string.Equals(key.KeyHash, presentedKeyHash, StringComparison.Ordinal))
        {
            return KeyRevokeOutcome.CurrentKey; // revoking the in-flight key would break this very session — rotate instead
        }

        key.RevokedAt = now;
        session.Store(key);
        await session.SaveChangesAsync(ct);
        return KeyRevokeOutcome.Revoked;
    }

    public async Task<TenantApiKey?> RotateKeyAsync(string tenantId, Guid oldKeyId, TenantApiKey replacement, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await using var session = store.LightweightSession();
        var old = await session.LoadAsync<TenantApiKey>(oldKeyId, ct);
        if (old is null || !string.Equals(old.TenantId, tenantId, StringComparison.Ordinal) || old.RevokedAt is not null)
        {
            return null; // unknown, belongs to another tenant, or already revoked — nothing minted is persisted
        }

        // One transaction: revoke the old key and store its replacement, so a reader never sees a gap where the tenant has
        // no active key. The replacement inherits the rotated key's label (it is the same logical key, re-issued).
        old.RevokedAt = now;
        replacement.Label = old.Label;
        session.Store(old);
        session.Store(replacement);
        await session.SaveChangesAsync(ct);
        return replacement;
    }

    public async Task<IReadOnlyList<TenantApiKey>> ListKeysAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return await session.Query<TenantApiKey>()
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ResolvedTenantKey?> ResolveKeyAsync(string keyHash, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var key = await session.Query<TenantApiKey>()
            .Where(k => k.KeyHash == keyHash && k.RevokedAt == null)
            .FirstOrDefaultAsync(ct);
        if (key is null)
        {
            return null;
        }

        var tenant = await session.LoadAsync<RegistryTenant>(key.TenantId, ct);
        return tenant is null
            ? null // a key with no tenant is unusable — treated as no match (falls through to the env fallback)
            : new ResolvedTenantKey(key.Id, Snapshot(tenant));
    }

    public async Task TouchLastUsedAsync(Guid keyId, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var key = await session.LoadAsync<TenantApiKey>(keyId, ct);
        if (key is null)
        {
            return;
        }

        key.LastUsedAt = now;
        session.Store(key);
        await session.SaveChangesAsync(ct);
    }

    internal static RegistryTenantSnapshot Snapshot(RegistryTenant tenant) =>
        new(tenant.Id, tenant.Actor, tenant.Status, tenant.SlotAllowance);
}
