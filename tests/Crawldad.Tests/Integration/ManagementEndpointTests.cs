using System.Text.Json;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Tenancy;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Crawldad.Tests.Integration;

/// <summary>The interim management surface end to end (tenant + key administration behind the constant-time management
/// key) and the DB-backed auth resolution it drives: an issued key authenticates a normal request, a revoke or suspend
/// stops it immediately (cache invalidation), the env-configured tenants still work, and a registry tenant's slot
/// allowance surfaces as the admission concurrency override. All keys here are synthetic test values.</summary>
[Collection(ManagementCollection.Name)]
public sealed class ManagementEndpointTests(ManagementFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private IAlbaHost Host => fixture.Host;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync(); // isolate each test on the shared single-tenanted registry
    public Task DisposeAsync() => Task.CompletedTask;

    // ---- management-key auth -------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejects_a_management_request_with_no_key() =>
        await Host.Scenario(x =>
        {
            x.Post.Json(new { id = "x", displayName = "X" }).ToUrl("/management/tenants");
            x.StatusCodeShouldBe(StatusCodes.Status401Unauthorized);
        });

    [Fact]
    public async Task Rejects_a_management_request_with_a_wrong_key() =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer("mgmt-key-WRONG-0123456789abcdef"));
            x.Get.Url("/management/tenants/whatever");
            x.StatusCodeShouldBe(StatusCodes.Status401Unauthorized);
        });

    // ---- create / get --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creates_a_tenant_and_reads_it_back()
    {
        var id = NewTenantId();
        var created = await CreateTenantAsync(new { id, displayName = "Acme Corp", actor = "ops@acme", tier = "pro", slotAllowance = 12 }, StatusCodes.Status201Created);

        created.GetProperty("id").GetString().ShouldBe(id);
        created.GetProperty("displayName").GetString().ShouldBe("Acme Corp");
        created.GetProperty("actor").GetString().ShouldBe("ops@acme");
        created.GetProperty("status").GetString().ShouldBe("active");
        created.GetProperty("tier").GetString().ShouldBe("pro");
        created.GetProperty("slotAllowance").GetInt32().ShouldBe(12);

        var fetched = await GetAsync($"/management/tenants/{id}", StatusCodes.Status200OK);
        fetched.GetProperty("id").GetString().ShouldBe(id);
        fetched.GetProperty("status").GetString().ShouldBe("active");
    }

    [Fact]
    public async Task Defaults_actor_to_the_id_and_leaves_slot_allowance_unset()
    {
        var id = NewTenantId();
        var created = await CreateTenantAsync(new { id, displayName = "No Frills" }, StatusCodes.Status201Created);

        created.GetProperty("actor").GetString().ShouldBe(id); // actor defaults to the id
        created.GetProperty("tier").GetString().ShouldBe("");
        created.GetProperty("slotAllowance").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Rejects_a_duplicate_tenant_id()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "First" }, StatusCodes.Status201Created);
        await CreateTenantAsync(new { id, displayName = "Second" }, StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Rejects_an_invalid_tenant_body() => // every field guard trips at once: bad id, empty name, over-long tier, non-positive allowance
        await CreateTenantAsync(new { id = "Not A Slug", displayName = "", tier = new string('x', TenantRules.MaxTierLength + 1), slotAllowance = 0 }, StatusCodes.Status400BadRequest);

    [Fact]
    public async Task Returns_404_for_an_unknown_tenant() =>
        await GetAsync("/management/tenants/does-not-exist", StatusCodes.Status404NotFound);

    // ---- keys ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Issues_a_key_returning_the_raw_secret_once_and_storing_only_its_hash()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Keyed" }, StatusCodes.Status201Created);

        var issued = await IssueKeyAsync(id);
        var raw = issued.GetProperty("apiKey").GetString()!;
        var keyId = issued.GetProperty("keyId").GetGuid();

        raw.ShouldStartWith($"ck_{ManagementFixture.KeyEnvLabel}_");
        issued.GetProperty("prefix").GetString().ShouldBe(raw[..issued.GetProperty("prefix").GetString()!.Length]);

        // The persisted document holds only the hash — never the raw key.
        await using var session = Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var doc = await session.LoadAsync<TenantApiKey>(keyId, _ct);
        doc.ShouldNotBeNull();
        doc!.KeyHash.ShouldBe(ApiKeyMint.Hash(raw));
        doc.KeyHash.ShouldNotBe(raw);
        JsonSerializer.Serialize(doc).ShouldNotContain(raw);
    }

    [Fact]
    public async Task Rejects_issuing_a_key_for_an_unknown_tenant() =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Post.Json(new { }).ToUrl("/management/tenants/ghost/keys");
            x.StatusCodeShouldBe(StatusCodes.Status404NotFound);
        });

    [Fact]
    public async Task Lists_keys_as_prefixes_only_newest_first()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Rotator" }, StatusCodes.Status201Created);
        var first = (await IssueKeyAsync(id)).GetProperty("apiKey").GetString()!;
        var second = (await IssueKeyAsync(id)).GetProperty("apiKey").GetString()!;

        var list = await GetAsync($"/management/tenants/{id}/keys", StatusCodes.Status200OK);
        var keys = list.GetProperty("keys").EnumerateArray().ToList();
        keys.Count.ShouldBe(2);
        foreach (var key in keys)
        {
            key.GetProperty("prefix").GetString().ShouldStartWith($"ck_{ManagementFixture.KeyEnvLabel}_");
            key.GetProperty("active").GetBoolean().ShouldBeTrue();
            key.TryGetProperty("keyHash", out _).ShouldBeFalse(); // no hash in a listing
        }

        var raw = list.GetRawText();
        raw.ShouldNotContain(first);  // never the raw keys
        raw.ShouldNotContain(second);
    }

    [Fact]
    public async Task Lists_no_keys_for_a_fresh_tenant()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Fresh" }, StatusCodes.Status201Created);

        var list = await GetAsync($"/management/tenants/{id}/keys", StatusCodes.Status200OK);
        list.GetProperty("keys").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Returns_404_listing_keys_for_an_unknown_tenant() =>
        await GetAsync("/management/tenants/ghost/keys", StatusCodes.Status404NotFound);

    [Fact]
    public async Task Revokes_a_key_and_reflects_it_in_the_listing()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Revoker" }, StatusCodes.Status201Created);
        var keyId = (await IssueKeyAsync(id)).GetProperty("keyId").GetGuid();

        await RevokeAsync(id, keyId, StatusCodes.Status204NoContent);

        var list = await GetAsync($"/management/tenants/{id}/keys", StatusCodes.Status200OK);
        var key = list.GetProperty("keys")[0];
        key.GetProperty("active").GetBoolean().ShouldBeFalse();
        key.GetProperty("revokedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Revoking_an_unknown_key_is_404()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "T" }, StatusCodes.Status201Created);
        await RevokeAsync(id, Guid.NewGuid(), StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Revoking_a_key_that_belongs_to_another_tenant_is_404()
    {
        var owner = NewTenantId();
        var other = NewTenantId();
        await CreateTenantAsync(new { id = owner, displayName = "Owner" }, StatusCodes.Status201Created);
        await CreateTenantAsync(new { id = other, displayName = "Other" }, StatusCodes.Status201Created);
        var keyId = (await IssueKeyAsync(owner)).GetProperty("keyId").GetGuid();

        await RevokeAsync(other, keyId, StatusCodes.Status404NotFound); // right key id, wrong tenant
    }

    [Fact]
    public async Task Revoking_an_already_revoked_key_is_404()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "T" }, StatusCodes.Status201Created);
        var keyId = (await IssueKeyAsync(id)).GetProperty("keyId").GetGuid();

        await RevokeAsync(id, keyId, StatusCodes.Status204NoContent);
        await RevokeAsync(id, keyId, StatusCodes.Status404NotFound); // idempotent — the second revoke is a no-op 404
    }

    // ---- suspend / reactivate ------------------------------------------------------------------------------------

    [Fact]
    public async Task Suspends_and_reactivates_a_tenant()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Toggling" }, StatusCodes.Status201Created);

        var suspended = await PostAsync($"/management/tenants/{id}/suspend", StatusCodes.Status200OK);
        suspended.GetProperty("status").GetString().ShouldBe("suspended");

        var reactivated = await PostAsync($"/management/tenants/{id}/reactivate", StatusCodes.Status200OK);
        reactivated.GetProperty("status").GetString().ShouldBe("active");
    }

    [Fact]
    public async Task Suspending_an_unknown_tenant_is_404() =>
        await PostAsync("/management/tenants/ghost/suspend", StatusCodes.Status404NotFound);

    // ---- DB-backed auth resolution through the real pipeline -----------------------------------------------------

    [Fact]
    public async Task An_issued_registry_key_authenticates_a_normal_request_and_records_last_used()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Live" }, StatusCodes.Status201Created);
        var raw = (await IssueKeyAsync(id)).GetProperty("apiKey").GetString()!;

        await AuthenticatedGetAsync("/payloads", raw, StatusCodes.Status200OK); // resolves via the registry

        var list = await GetAsync($"/management/tenants/{id}/keys", StatusCodes.Status200OK);
        list.GetProperty("keys")[0].GetProperty("lastUsedAt").ValueKind.ShouldNotBe(JsonValueKind.Null); // best-effort touch landed
    }

    [Fact]
    public async Task A_revoked_key_stops_authenticating_immediately()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Rev" }, StatusCodes.Status201Created);
        var issued = await IssueKeyAsync(id);
        var raw = issued.GetProperty("apiKey").GetString()!;
        var keyId = issued.GetProperty("keyId").GetGuid();

        await AuthenticatedGetAsync("/payloads", raw, StatusCodes.Status200OK); // works + caches
        await RevokeAsync(id, keyId, StatusCodes.Status204NoContent);           // invalidates the cache
        await AuthenticatedGetAsync("/payloads", raw, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task A_suspended_tenant_is_rejected_then_restored_on_reactivation()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Susp" }, StatusCodes.Status201Created);
        var raw = (await IssueKeyAsync(id)).GetProperty("apiKey").GetString()!;

        await AuthenticatedGetAsync("/payloads", raw, StatusCodes.Status200OK);
        await PostAsync($"/management/tenants/{id}/suspend", StatusCodes.Status200OK);
        await AuthenticatedGetAsync("/payloads", raw, StatusCodes.Status401Unauthorized); // suspended → rejected
        await PostAsync($"/management/tenants/{id}/reactivate", StatusCodes.Status200OK);
        await AuthenticatedGetAsync("/payloads", raw, StatusCodes.Status200OK);            // restored
    }

    [Fact]
    public async Task An_env_configured_key_still_authenticates_via_the_fallback() =>
        await AuthenticatedGetAsync("/payloads", TestTenants.PrimaryKey, StatusCodes.Status200OK);

    [Fact]
    public async Task A_registry_tenants_slot_allowance_is_resolvable_by_the_admission_gate_without_auth()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Big", slotAllowance = 3 }, StatusCodes.Status201Created);
        await IssueKeyAsync(id);

        // The background promotion path: prime the gate straight from the store — no auth involved — and read the cap the
        // gate reads. This is what PromoteOldestAsync does before TryAdmit, so a registry cap holds long after (or without) auth.
        var gate = Host.Services.GetRequiredService<IRunAdmissionGate>();
        await gate.PrimeAsync(id, _ct);

        Host.Services.GetRequiredService<ITenantConcurrencyOverrides>().TryGetConcurrencyOverride(id, out var limit).ShouldBeTrue();
        limit.ShouldBe(3);
    }

    [Fact]
    public async Task Background_promotion_honours_a_registry_cap_on_a_cold_override_cache()
    {
        // The regression this guards: a run that outlives the auth-cache TTL is promoted from the background queue handler,
        // whose cap resolution must come from the store — not a lapsed auth cache that would silently revert to the global 32.
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Capped", slotAllowance = 1 }, StatusCodes.Status201Created);

        var gate = Host.Services.GetRequiredService<IRunAdmissionGate>();
        var queue = Host.Services.GetRequiredService<RunQueue>();
        var directory = Host.Services.GetRequiredService<TenantDirectory>();
        var store = Host.Services.GetRequiredService<IDocumentStore>();

        // Fill the tenant's single slot exactly as a running run would (no auth involved), then enqueue one behind it.
        await gate.PrimeAsync(id, _ct);
        gate.TryAdmit(id, Guid.NewGuid()).ShouldBeTrue(); // cap 1 → now full
        await using (var session = store.LightweightSession(id))
        {
            session.Store(new QueuedRun { Id = Guid.NewGuid(), Sequence = 1, PayloadName = "p", QueuedAt = FakeClock.Fixed });
            await session.SaveChangesAsync(_ct);
        }

        // Drop the override cache so promotion must re-resolve the cap from the store (the lapsed-TTL / post-restart case).
        directory.InvalidateTenant(id);

        await using var scope = Host.Services.CreateAsyncScope();
        var promoted = await queue.PromoteOldestAsync(scope.ServiceProvider.GetRequiredService<IMessageBus>(), id, _ct);

        promoted.ShouldBeFalse(); // the cap of 1 is full → nothing promoted (a reverted global cap of 32 would have promoted it)
        await using var read = store.QuerySession(id);
        (await read.Query<QueuedRun>().CountAsync(_ct)).ShouldBe(1); // the run is still queued
    }

    // ---- store branches not reachable through an endpoint --------------------------------------------------------

    [Fact]
    public async Task Resolving_a_key_whose_tenant_is_missing_yields_no_match()
    {
        var store = Host.Services.GetRequiredService<ITenantRegistryStore>();
        var raw = ApiKeyMint.Issue(ManagementFixture.KeyEnvLabel);
        await store.AddKeyAsync(new TenantApiKey { Id = Guid.NewGuid(), TenantId = "ghost-tenant", KeyHash = raw.Hash, Prefix = raw.Prefix, CreatedAt = FakeClock.Fixed }, _ct);

        (await store.ResolveKeyAsync(raw.Hash, _ct)).ShouldBeNull(); // key exists but its tenant doesn't → dangling
    }

    [Fact]
    public async Task Touching_an_unknown_key_is_a_no_op()
    {
        var store = Host.Services.GetRequiredService<ITenantRegistryStore>();
        await store.TouchLastUsedAsync(Guid.NewGuid(), FakeClock.Fixed, _ct); // must not throw
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string NewTenantId() => "t-" + Guid.NewGuid().ToString("N");

    private async Task<JsonElement> CreateTenantAsync(object body, int expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Post.Json(body).ToUrl("/management/tenants");
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private Task<JsonElement> IssueKeyAsync(string id) => PostAsync($"/management/tenants/{id}/keys", StatusCodes.Status201Created);

    private async Task<JsonElement> PostAsync(string url, int expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Post.Json(new { }).ToUrl(url);
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetAsync(string url, int expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Get.Url(url);
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task RevokeAsync(string id, Guid keyId, int expected) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Delete.Url($"/management/tenants/{id}/keys/{keyId}");
            x.StatusCodeShouldBe(expected);
        });

    private async Task AuthenticatedGetAsync(string url, string apiKey, int expected) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Get.Url(url);
            x.StatusCodeShouldBe(expected);
        });
}
