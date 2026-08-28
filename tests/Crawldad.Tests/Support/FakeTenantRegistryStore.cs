using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Support;

/// <summary>An in-memory <see cref="ITenantRegistryStore"/> for driving <see cref="TenantDirectory"/> unit tests: it maps
/// a presented key hash to a resolved tenant, counts resolutions and last-used touches, and can be told to fault the
/// touch — so the directory's cache, fallback, suspension, and best-effort branches are exercised without a database.
/// Only <see cref="ResolveKeyAsync"/> and <see cref="TouchLastUsedAsync"/> are used by the directory; the rest throw.</summary>
internal sealed class FakeTenantRegistryStore : ITenantRegistryStore
{
    private readonly Dictionary<string, ResolvedTenantKey> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistryTenant> _byId = new(StringComparer.Ordinal);

    /// <summary>How many times a key was resolved from the store (a cold, uncached lookup).</summary>
    public int ResolveCalls { get; private set; }

    /// <summary>How many times a tenant was loaded by id (the admission-override prime hits this).</summary>
    public int FindCalls { get; private set; }

    /// <summary>How many times a last-used touch was attempted.</summary>
    public int TouchCalls { get; private set; }

    /// <summary>When set, the next <see cref="TouchLastUsedAsync"/> throws — to prove a best-effort write failure never
    /// fails authentication.</summary>
    public bool ThrowOnTouch { get; set; }

    /// <summary>Registers an active tenant's key and returns the synthetic raw key to present.</summary>
    public string AddActiveTenantKey(string tenantId, string? actor = null, int? slotAllowance = null, string? tier = null) =>
        AddTenantKey(tenantId, TenantStatus.Active, actor, slotAllowance, tier);

    /// <summary>Registers a tenant's key at the given status (and a matching tenant record) and returns the synthetic raw key.</summary>
    public string AddTenantKey(string tenantId, TenantStatus status, string? actor = null, int? slotAllowance = null, string? tier = null)
    {
        var raw = $"ck_test_{tenantId}_{Guid.NewGuid():N}"; // synthetic, clearly fake
        var resolvedActor = actor ?? $"{tenantId}@actor";
        _byHash[ApiKeyMint.Hash(raw)] = new ResolvedTenantKey(Guid.NewGuid(), new RegistryTenantSnapshot(tenantId, resolvedActor, status, slotAllowance));
        _byId[tenantId] = new RegistryTenant { Id = tenantId, Actor = resolvedActor, Status = status, SlotAllowance = slotAllowance, Tier = tier ?? "" };
        return raw;
    }

    /// <summary>Mutates a registered tenant's slot allowance (to prove a change is picked up after InvalidateTenant + re-prime).</summary>
    public void SetAllowance(string tenantId, int? slotAllowance) => _byId[tenantId].SlotAllowance = slotAllowance;

    public Task<ResolvedTenantKey?> ResolveKeyAsync(string keyHash, CancellationToken ct)
    {
        ResolveCalls++;
        return Task.FromResult(_byHash.TryGetValue(keyHash, out var resolved) ? resolved : (ResolvedTenantKey?)null);
    }

    public Task<RegistryTenant?> FindAsync(string tenantId, CancellationToken ct)
    {
        FindCalls++;
        return Task.FromResult(_byId.TryGetValue(tenantId, out var tenant) ? tenant : null);
    }

    public Task TouchLastUsedAsync(Guid keyId, DateTimeOffset now, CancellationToken ct)
    {
        TouchCalls++;
        return ThrowOnTouch ? throw new InvalidOperationException("simulated last-used write failure") : Task.CompletedTask;
    }

    public Task<bool> CreateAsync(RegistryTenant tenant, CancellationToken ct) => throw new NotSupportedException();

    public Task<RegistryTenant?> SetStatusAsync(string tenantId, TenantStatus status, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();

    public Task<RegistryTenant?> SetPlanAsync(string tenantId, string tier, int? slotAllowance, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();

    public Task AddKeyAsync(TenantApiKey key, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> RevokeKeyAsync(string tenantId, Guid keyId, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();

    public Task<TenantApiKey?> RotateKeyAsync(string tenantId, Guid oldKeyId, TenantApiKey replacement, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<TenantApiKey>> ListKeysAsync(string tenantId, CancellationToken ct) => throw new NotSupportedException();
}
