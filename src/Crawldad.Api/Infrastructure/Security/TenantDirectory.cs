using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>Resolves a presented API key to its tenant. Implemented by the composite <see cref="TenantDirectory"/>, which
/// consults the DB-backed registry (cached) and falls back to the env-configured tenants.</summary>
public interface ITenantAuthenticator
{
    /// <summary>Resolves <paramref name="presentedKey"/> to its authenticated tenant, or null when it matches no active
    /// key of a live tenant. A suspended registry tenant resolves to null (rejected like an unknown key).</summary>
    Task<AuthenticatedTenant?> AuthenticateAsync(string presentedKey, CancellationToken ct);
}

/// <summary>Supplies a tenant's per-tenant concurrent-run override (its slot allowance) to the admission gate. Both the
/// env <see cref="TenantRegistry"/> and the composite <see cref="TenantDirectory"/> implement it, so the gate honours an
/// override from either source without knowing which.</summary>
public interface ITenantConcurrencyOverrides
{
    /// <summary>Resolves the tenant's override into an invalidation-driven cache so that a subsequent, <b>synchronous</b>
    /// <see cref="TryGetConcurrencyOverride"/> is authoritative — <b>without</b> depending on the short-TTL auth cache.
    /// The admission call sites await this before admitting, so the background promotion path (which can run long after —
    /// or entirely without — a recent auth, e.g. restart recovery) honours a registry tenant's cap. A no-op for the env
    /// directory (its overrides are already in memory).</summary>
    Task PrimeAsync(string tenantId, CancellationToken ct);

    /// <summary>The tenant's configured concurrent-run override, or false to defer to the global default. Reads only the
    /// invalidation-driven admission cache (warmed by <see cref="PrimeAsync"/>) and the env overrides — never the auth cache.</summary>
    bool TryGetConcurrencyOverride(string tenantId, out int limit);
}

/// <summary>Knobs for the DB-backed tenant registry, bound from <c>Crawldad:Registry</c>.</summary>
public sealed class TenantRegistryOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Crawldad:Registry";

    /// <summary>The deployment env moniker embedded in every minted key (<c>ck_&lt;label&gt;_…</c>) so a key is
    /// recognisable and a staging key is never mistaken for a prod one. Cosmetic/namespacing only.</summary>
    public string KeyEnvironmentLabel { get; init; } = "dev";

    /// <summary>The auth cache TTL, in seconds. Short so a revocation or suspension made on another instance takes effect
    /// within this bound; a revoke/suspend on <b>this</b> instance invalidates immediately. 0 disables caching (every
    /// request re-resolves from the store).</summary>
    public int CacheTtlSeconds { get; init; } = 30;
}

/// <summary>The tenant directory: the single seam the auth handler and admission gate resolve tenants through. A presented
/// key is matched against the DB-backed registry — behind a short-TTL, revocation-safe in-process auth cache — and, when it
/// matches no registry key, against the env-configured <see cref="TenantRegistry"/>, so existing staging/beta wiring keeps
/// working unchanged. A suspended tenant is rejected at auth. A registry tenant's slot allowance flows into the admission
/// gate's per-tenant override via a <b>separate</b>, invalidation-driven admission cache resolved from the store by
/// <see cref="PrimeAsync"/> — deliberately NOT the auth cache, so the background promotion path honours the cap regardless
/// of TTL or whether an auth even happened on this instance.</summary>
public sealed class TenantDirectory : ITenantAuthenticator, ITenantConcurrencyOverrides
{
    private readonly TenantRegistry _env;
    private readonly ITenantRegistryStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<TenantDirectory> _logger;
    private readonly TimeSpan _ttl;

    // The auth cache: positive key resolutions only (a negative lookup is NEVER cached, so a flood of junk keys cannot grow
    // it), each with a short expiry for revocation-safety. Expired entries are ignored on read and swept opportunistically.
    private readonly ConcurrentDictionary<string, CachedKey> _byKeyHash = new(StringComparer.Ordinal);

    // The admission-override cache: tenant id -> slot allowance (null = not a registry tenant, or one with no override).
    // Resolved from the store by PrimeAsync and evicted ONLY by InvalidateTenant — no TTL — so a run that outlives the auth
    // TTL, and a promotion re-driven after a restart with no prior auth, both still resolve the tenant's real cap.
    private readonly ConcurrentDictionary<string, int?> _admissionOverride = new(StringComparer.Ordinal);

    /// <summary>Builds the directory over the env registry (the fallback) and the registry store (the primary).</summary>
    public TenantDirectory(TenantRegistry env, ITenantRegistryStore store, TimeProvider clock, IOptions<TenantRegistryOptions> options, ILogger<TenantDirectory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _env = env;
        _store = store;
        _clock = clock;
        _logger = logger;
        _ttl = TimeSpan.FromSeconds(options.Value.CacheTtlSeconds);
    }

    /// <inheritdoc />
    public async Task<AuthenticatedTenant?> AuthenticateAsync(string presentedKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(presentedKey);
        var now = _clock.GetUtcNow();
        var hash = ApiKeyMint.Hash(presentedKey);

        // 1) Warm registry cache — no store round-trip.
        if (_byKeyHash.TryGetValue(hash, out var cached) && cached.ExpiresAt > now)
        {
            return Authenticated(cached.Tenant);
        }

        // 2) Env-configured tenants: an in-memory hit here means a registry key never reaches the store (env keys and
        //    registry keys are disjoint by construction, so probing env before the store's cold path only avoids I/O).
        if (_env.TryAuthenticate(presentedKey, out var envTenant))
        {
            return envTenant;
        }

        // 3) Registry store (cold): resolve, cache the positive result, and enforce suspension.
        var resolved = await _store.ResolveKeyAsync(hash, ct);
        if (resolved is not { } hit)
        {
            return null; // matched neither the registry nor the env directory
        }

        PruneExpiredKeys(now); // bound the auth cache before adding a fresh entry
        _byKeyHash[hash] = new CachedKey(hit.Tenant, now + _ttl);

        if (hit.Tenant.Status != TenantStatus.Active)
        {
            return null; // suspended → rejected like an unknown key
        }

        await TouchLastUsedAsync(hit.KeyId, now, ct);
        return new AuthenticatedTenant(hit.Tenant.Id, hit.Tenant.Actor);
    }

    /// <inheritdoc />
    public async Task PrimeAsync(string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (_admissionOverride.ContainsKey(tenantId))
        {
            return; // already resolved; the entry is dropped only by InvalidateTenant, so it is never silently stale
        }

        // Resolve the tenant's allowance from the store (not the auth cache) and cache it. Null covers both "not a registry
        // tenant" and "a registry tenant with no override" — TryGetConcurrencyOverride then defers to the env/global default.
        var tenant = await _store.FindAsync(tenantId, ct);
        _admissionOverride[tenantId] = tenant?.SlotAllowance;
    }

    /// <inheritdoc />
    public bool TryGetConcurrencyOverride(string tenantId, out int limit)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        // The invalidation-driven admission cache (warmed by PrimeAsync from the store) is authoritative for a registry
        // tenant's allowance — independent of the auth cache's TTL. A null/absent entry, or an env tenant, defers to the
        // env override (and, on a miss there, the global default).
        if (_admissionOverride.TryGetValue(tenantId, out var cached) && cached is { } allowance)
        {
            limit = allowance;
            return true;
        }

        return _env.TryGetConcurrencyOverride(tenantId, out limit);
    }

    /// <summary>Drops every cached entry for a tenant — its admission override and all of its keys. Called in-process the
    /// instant a management op revokes a key or changes a tenant's status (or allowance), so the change is honoured
    /// immediately here; the short auth-cache TTL bounds staleness on other instances.</summary>
    public void InvalidateTenant(string tenantId)
    {
        _admissionOverride.TryRemove(tenantId, out _);
        foreach (var entry in _byKeyHash)
        {
            if (string.Equals(entry.Value.Tenant.Id, tenantId, StringComparison.Ordinal))
            {
                _byKeyHash.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>The number of entries currently held in the auth cache — for tests asserting the expired-entry sweep.</summary>
    internal int CachedKeyCount => _byKeyHash.Count;

    /// <summary>The auth-cache size past which a cold write first sweeps expired entries (so a long-lived process that sees
    /// many one-time keys does not accumulate stale entries; entries for still-used keys are refreshed in place, not swept).</summary>
    internal const int PruneKeyCacheAbove = 64;

    // Sweep expired entries when the map has grown past the floor. Only expired entries go — an active key's entry (fresh
    // expiry) stays — so this bounds growth without ever evicting a live resolution.
    private void PruneExpiredKeys(DateTimeOffset now)
    {
        if (_byKeyHash.Count <= PruneKeyCacheAbove)
        {
            return;
        }

        foreach (var entry in _byKeyHash)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _byKeyHash.TryRemove(entry.Key, out _);
            }
        }
    }

    private static AuthenticatedTenant? Authenticated(RegistryTenantSnapshot tenant) =>
        tenant.Status == TenantStatus.Active ? new AuthenticatedTenant(tenant.Id, tenant.Actor) : null;

    // Best-effort: last-used is advisory, so a store failure here is swallowed rather than failing an otherwise-valid
    // authentication. Throttled to cache-miss frequency (this runs only on a cold resolution, ~once per TTL per key).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A last-used stamp is advisory telemetry on the hot auth path; any store fault (or a request-cancellation surfaced mid-write) must not fail an authentication that has already succeeded — the stamp is simply skipped until the next cold resolution.")]
    private async Task TouchLastUsedAsync(Guid keyId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await _store.TouchLastUsedAsync(keyId, now, ct);
        }
        catch (Exception ex)
        {
            // Advisory only — never let a last-used write fail the request; note it and move on.
            _logger.LogDebug(ex, "best-effort last-used update for key {KeyId} failed; skipping", keyId);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct CachedKey(RegistryTenantSnapshot Tenant, DateTimeOffset ExpiresAt);
}
