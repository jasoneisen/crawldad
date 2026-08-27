using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The composite tenant directory: a presented key resolves against the DB-backed registry (behind a short-TTL,
/// revocation-safe cache) and falls back to the env-configured tenants; a suspended tenant is rejected; a registry
/// tenant's slot allowance surfaces as the admission gate's per-tenant override. Driven against a fake store + an
/// advanceable clock so every branch is deterministic. (All keys here are synthetic test values.)</summary>
public class TenantDirectoryTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task Resolves_a_registry_key_to_its_tenant_and_records_last_used()
    {
        var (dir, store, _) = Build();
        var key = store.AddActiveTenantKey("acme", actor: "ops@acme");

        var tenant = await dir.AuthenticateAsync(key, _ct);

        tenant.ShouldNotBeNull();
        tenant!.Value.Id.ShouldBe("acme");
        tenant.Value.Actor.ShouldBe("ops@acme");
        store.TouchCalls.ShouldBe(1); // last-used advanced once, on the cold resolution
    }

    [Fact]
    public async Task Caches_a_registry_hit_so_a_second_auth_skips_the_store()
    {
        var (dir, store, _) = Build();
        var key = store.AddActiveTenantKey("acme");

        await dir.AuthenticateAsync(key, _ct);
        await dir.AuthenticateAsync(key, _ct);

        store.ResolveCalls.ShouldBe(1); // second auth answered from cache
        store.TouchCalls.ShouldBe(1);   // and no repeat last-used write on a cache hit
    }

    [Fact]
    public async Task Re_resolves_from_the_store_after_the_cache_ttl_lapses()
    {
        var (dir, store, clock) = Build(ttlSeconds: 30);
        var key = store.AddActiveTenantKey("acme");

        await dir.AuthenticateAsync(key, _ct);
        clock.Now = clock.Now.AddSeconds(31);
        await dir.AuthenticateAsync(key, _ct);

        store.ResolveCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Disables_caching_when_the_ttl_is_zero()
    {
        var (dir, store, _) = Build(ttlSeconds: 0);
        var key = store.AddActiveTenantKey("acme");

        await dir.AuthenticateAsync(key, _ct);
        await dir.AuthenticateAsync(key, _ct);

        store.ResolveCalls.ShouldBe(2); // every request re-resolves
    }

    [Fact]
    public async Task Rejects_a_suspended_tenant_without_touching_last_used()
    {
        var (dir, store, _) = Build();
        var key = store.AddTenantKey("frozen", TenantStatus.Suspended);

        (await dir.AuthenticateAsync(key, _ct)).ShouldBeNull();
        store.TouchCalls.ShouldBe(0);

        // The suspended verdict is cached too — a second attempt is still rejected without a re-query.
        (await dir.AuthenticateAsync(key, _ct)).ShouldBeNull();
        store.ResolveCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Falls_back_to_the_env_directory_for_a_configured_key()
    {
        const string envKey = "env-key-0123456789abcdef";
        var (dir, store, _) = Build(envTenants: EnvTenant("envco", envKey, actor: "env@co"));

        var tenant = await dir.AuthenticateAsync(envKey, _ct);

        tenant.ShouldNotBeNull();
        tenant!.Value.Id.ShouldBe("envco");
        tenant.Value.Actor.ShouldBe("env@co");
        store.ResolveCalls.ShouldBe(0); // an env hit never reaches the store
    }

    [Fact]
    public async Task Returns_null_when_neither_the_registry_nor_the_env_directory_match()
    {
        var (dir, store, _) = Build(envTenants: EnvTenant("envco", "env-key-0123456789abcdef"));

        (await dir.AuthenticateAsync("ck_test_not-a-real-key-000000", _ct)).ShouldBeNull();
        store.ResolveCalls.ShouldBe(1); // env miss → store cold path → also a miss
    }

    [Fact]
    public async Task A_best_effort_last_used_failure_never_fails_authentication()
    {
        var (dir, store, _) = Build();
        store.ThrowOnTouch = true;
        var key = store.AddActiveTenantKey("acme");

        var tenant = await dir.AuthenticateAsync(key, _ct);

        tenant.ShouldNotBeNull(); // the store fault on the advisory write is swallowed
        tenant!.Value.Id.ShouldBe("acme");
        store.TouchCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Resolves_a_registry_slot_allowance_from_the_store_via_prime_without_any_auth()
    {
        // The background promotion path: no auth has warmed anything — priming must still resolve the registry cap.
        var (dir, store, _) = Build();
        store.AddActiveTenantKey("big", slotAllowance: 5);

        await dir.PrimeAsync("big", _ct);

        dir.TryGetConcurrencyOverride("big", out var limit).ShouldBeTrue();
        limit.ShouldBe(5);
        store.FindCalls.ShouldBe(1); // resolved from the store, not the auth cache
    }

    [Fact]
    public async Task Honours_the_registry_cap_even_after_the_auth_cache_has_expired()
    {
        // A long-running run outlives the auth TTL; promotion primes and must still see the registry cap (the bug: it used
        // to fall back to the global default here).
        var (dir, store, clock) = Build(ttlSeconds: 30);
        var key = store.AddActiveTenantKey("big", slotAllowance: 5);
        await dir.AuthenticateAsync(key, _ct);
        clock.Now = clock.Now.AddSeconds(31); // the auth cache entry is now expired

        await dir.PrimeAsync("big", _ct);

        dir.TryGetConcurrencyOverride("big", out var limit).ShouldBeTrue();
        limit.ShouldBe(5);
    }

    [Fact]
    public async Task Primes_the_override_from_the_store_once_then_serves_it_from_cache()
    {
        var (dir, store, _) = Build();
        store.AddActiveTenantKey("big", slotAllowance: 5);

        await dir.PrimeAsync("big", _ct);
        await dir.PrimeAsync("big", _ct);

        store.FindCalls.ShouldBe(1); // second prime is a cache hit
    }

    [Fact]
    public async Task Defers_the_override_when_a_primed_registry_tenant_has_no_allowance()
    {
        var (dir, store, _) = Build();
        store.AddActiveTenantKey("std", slotAllowance: null);
        await dir.PrimeAsync("std", _ct);

        dir.TryGetConcurrencyOverride("std", out var limit).ShouldBeFalse();
        limit.ShouldBe(0);
    }

    [Fact]
    public void Falls_back_to_the_env_concurrency_override_without_priming()
    {
        var (dir, _, _) = Build(envTenants: EnvTenant("envco", "env-key-0123456789abcdef", maxConcurrentRuns: 7));

        dir.TryGetConcurrencyOverride("envco", out var limit).ShouldBeTrue();
        limit.ShouldBe(7);
    }

    [Fact]
    public async Task Defers_the_override_for_an_unknown_tenant_even_after_priming()
    {
        var (dir, store, _) = Build();

        await dir.PrimeAsync("ghost", _ct); // the store has no such tenant → caches null → defers

        dir.TryGetConcurrencyOverride("ghost", out var limit).ShouldBeFalse();
        limit.ShouldBe(0);
        store.FindCalls.ShouldBe(1);
    }

    [Fact]
    public async Task An_allowance_change_takes_effect_on_the_next_prime_after_invalidation()
    {
        var (dir, store, _) = Build();
        store.AddActiveTenantKey("scaling", slotAllowance: 5);
        await dir.PrimeAsync("scaling", _ct);

        store.SetAllowance("scaling", 10); // the durable allowance changes...
        dir.TryGetConcurrencyOverride("scaling", out var stale).ShouldBeTrue();
        stale.ShouldBe(5); // ...but the cached value stands until invalidation

        dir.InvalidateTenant("scaling");
        await dir.PrimeAsync("scaling", _ct);

        dir.TryGetConcurrencyOverride("scaling", out var fresh).ShouldBeTrue();
        fresh.ShouldBe(10);
    }

    [Fact]
    public async Task Invalidating_a_tenant_drops_only_its_cached_keys()
    {
        var (dir, store, _) = Build();
        var keyA = store.AddActiveTenantKey("a");
        var keyB = store.AddActiveTenantKey("b");
        await dir.AuthenticateAsync(keyA, _ct);
        await dir.AuthenticateAsync(keyB, _ct);
        store.ResolveCalls.ShouldBe(2);

        dir.InvalidateTenant("a");

        await dir.AuthenticateAsync(keyB, _ct); // b untouched → still cached
        store.ResolveCalls.ShouldBe(2);
        await dir.AuthenticateAsync(keyA, _ct); // a dropped → re-resolved
        store.ResolveCalls.ShouldBe(3);
    }

    [Fact]
    public async Task Does_not_cache_a_failed_key_lookup()
    {
        // A junk key must re-hit the store every time — negative results are never cached, so an attacker flooding random
        // keys cannot grow the cache.
        var (dir, store, _) = Build();

        (await dir.AuthenticateAsync("ck_test_junk-0000000000", _ct)).ShouldBeNull();
        (await dir.AuthenticateAsync("ck_test_junk-0000000000", _ct)).ShouldBeNull();

        store.ResolveCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Sweeps_expired_auth_cache_entries_once_the_map_grows_past_the_floor()
    {
        var (dir, store, clock) = Build(ttlSeconds: 30);
        for (var i = 0; i <= TenantDirectory.PruneKeyCacheAbove; i++) // one past the floor
        {
            await dir.AuthenticateAsync(store.AddActiveTenantKey($"t{i}"), _ct);
        }

        dir.CachedKeyCount.ShouldBe(TenantDirectory.PruneKeyCacheAbove + 1);
        clock.Now = clock.Now.AddSeconds(31); // every cached entry is now expired

        // A fresh cold resolution trips the sweep (count is over the floor), dropping the expired entries before adding this one.
        await dir.AuthenticateAsync(store.AddActiveTenantKey("fresh"), _ct);

        dir.CachedKeyCount.ShouldBe(1);
    }

    [Fact]
    public async Task Rejects_a_null_presented_key() =>
        await Should.ThrowAsync<ArgumentNullException>(async () => await Build().Directory.AuthenticateAsync(null!, _ct));

    [Fact]
    public async Task Rejects_a_null_tenant_id_on_prime() =>
        await Should.ThrowAsync<ArgumentNullException>(async () => await Build().Directory.PrimeAsync(null!, _ct));

    [Fact]
    public void Rejects_a_null_tenant_id_on_the_override_lookup() =>
        Should.Throw<ArgumentNullException>(() => Build().Directory.TryGetConcurrencyOverride(null!, out _));

    private static (TenantDirectory Directory, FakeTenantRegistryStore Store, MutableClock Clock) Build(
        int ttlSeconds = 30, params TenantDescriptor[] envTenants)
    {
        var env = new TenantRegistry(Options.Create(new TenantOptions { Tenants = envTenants }));
        var store = new FakeTenantRegistryStore();
        var clock = new MutableClock(FakeClock.Fixed);
        var options = Options.Create(new TenantRegistryOptions { CacheTtlSeconds = ttlSeconds });
        return (new TenantDirectory(env, store, clock, options, NullLogger<TenantDirectory>.Instance), store, clock);
    }

    private static TenantDescriptor EnvTenant(string id, string key, string actor = "env@actor", int? maxConcurrentRuns = null) =>
        new() { Id = id, ApiKey = key, Actor = actor, MaxConcurrentRuns = maxConcurrentRuns };
}
