using System.Text.Json;
using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Tenant self-service API key management (<c>/tenant/keys</c>) end to end, authenticated by the tenant's OWN
/// key. List / mint / rotate / revoke, the registry-only guard (an env tenant is a 400), the anti-lockout refusals
/// (last-key and current-key → 409, rotate is the escape hatch), strict self-scoping (a foreign key id is a 404), and
/// cache convergence (a revoked/rotated-out key stops authenticating within the test). Reuses the management surface only
/// to seed a registry tenant + its first key. All keys here are synthetic test values; no raw key is ever logged.</summary>
[Collection(ManagementCollection.Name)]
public sealed class TenantKeyEndpointTests(ManagementFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private IAlbaHost Host => fixture.Host;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync(); // isolate each test on the shared single-tenanted registry
    public Task DisposeAsync() => Task.CompletedTask;

    // ---- mint ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mints_a_labelled_key_that_authenticates_and_persists_only_its_hash()
    {
        var seed = await SeedTenantAsync();

        var minted = await MintAsync(seed.Key, new { label = "ci" });
        var raw = minted.GetProperty("apiKey").GetString()!;
        var keyId = minted.GetProperty("keyId").GetGuid();

        raw.ShouldStartWith($"ck_{ManagementFixture.KeyEnvLabel}_");
        minted.GetProperty("label").GetString().ShouldBe("ci");
        minted.GetProperty("prefix").GetString()!.ShouldNotBe(raw); // prefix is a display fragment, never the whole key

        // The self-service key really works — the whole point (a registry-backed key, not a dead key).
        await AuthProbeAsync(raw, StatusCodes.Status200OK);

        // Only the hash is persisted, never the raw key.
        await using var session = Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var doc = await session.LoadAsync<TenantApiKey>(keyId, _ct);
        doc.ShouldNotBeNull();
        doc!.KeyHash.ShouldBe(ApiKeyMint.Hash(raw));
        doc.Label.ShouldBe("ci");
        JsonSerializer.Serialize(doc).ShouldNotContain(raw);
    }

    [Fact]
    public async Task Mints_an_unlabelled_key_when_the_label_is_blank()
    {
        var seed = await SeedTenantAsync();

        var minted = await MintAsync(seed.Key, new { }); // no label
        minted.TryGetProperty("label", out _).ShouldBeFalse(); // omitted for an unlabelled key
        minted.GetProperty("apiKey").GetString().ShouldStartWith($"ck_{ManagementFixture.KeyEnvLabel}_");
    }

    [Fact]
    public async Task Rejects_a_too_long_label()
    {
        var seed = await SeedTenantAsync();

        var problem = await MintAsync(seed.Key, new { label = new string('x', 65) }, StatusCodes.Status400BadRequest);
        problem.GetProperty("errors").GetProperty("label").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Refuses_minting_for_an_env_tenant()
    {
        var problem = await MintAsync(TestTenants.PrimaryKey, new { }, StatusCodes.Status400BadRequest);
        problem.GetProperty("title").GetString().ShouldBe("self_service_unavailable");
    }

    // ---- list ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lists_keys_flagging_the_current_one_and_never_leaking_a_secret()
    {
        var seed = await SeedTenantAsync();
        var second = await MintAsync(seed.Key, new { label = "extra" });
        var secondRaw = second.GetProperty("apiKey").GetString()!;
        var secondId = second.GetProperty("keyId").GetGuid();

        var list = await ListAsync(seed.Key);
        var keys = list.GetProperty("keys").EnumerateArray().ToList();
        keys.Count.ShouldBe(2);

        var current = keys.Single(k => k.GetProperty("keyId").GetGuid() == seed.KeyId);
        current.GetProperty("current").GetBoolean().ShouldBeTrue();  // the key we're calling with
        current.GetProperty("active").GetBoolean().ShouldBeTrue();

        var other = keys.Single(k => k.GetProperty("keyId").GetGuid() == secondId);
        other.GetProperty("current").GetBoolean().ShouldBeFalse();
        other.GetProperty("active").GetBoolean().ShouldBeTrue();
        other.GetProperty("label").GetString().ShouldBe("extra");

        var raw = list.GetRawText();
        raw.ShouldNotContain(seed.Key);   // never a raw key
        raw.ShouldNotContain(secondRaw);
        raw.ShouldNotContain("keyHash");  // never a hash
    }

    [Fact]
    public async Task Reflects_a_revoked_key_as_inactive_in_the_listing()
    {
        var seed = await SeedTenantAsync();
        var second = await MintAsync(seed.Key, new { });
        var secondId = second.GetProperty("keyId").GetGuid();

        await DeleteKeyAsync(seed.Key, secondId, StatusCodes.Status204NoContent);

        var list = await ListAsync(seed.Key);
        var revoked = list.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("keyId").GetGuid() == secondId);
        revoked.GetProperty("active").GetBoolean().ShouldBeFalse();
        revoked.GetProperty("current").GetBoolean().ShouldBeFalse();
        revoked.GetProperty("revokedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Refuses_listing_for_an_env_tenant()
    {
        var problem = await ListAsync(TestTenants.PrimaryKey, StatusCodes.Status400BadRequest);
        problem.GetProperty("title").GetString().ShouldBe("self_service_unavailable");
    }

    // ---- rotate --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rotates_a_key_minting_a_replacement_that_inherits_the_label()
    {
        var seed = await SeedTenantAsync();
        var minted = await MintAsync(seed.Key, new { label = "ci" });
        var oldId = minted.GetProperty("keyId").GetGuid();

        var rotated = await RotateAsync(seed.Key, oldId);
        var newRaw = rotated.GetProperty("apiKey").GetString()!;
        rotated.GetProperty("keyId").GetGuid().ShouldNotBe(oldId);
        rotated.GetProperty("label").GetString().ShouldBe("ci"); // inherited from the rotated key

        await AuthProbeAsync(newRaw, StatusCodes.Status200OK); // the replacement authenticates

        var list = await ListAsync(seed.Key);
        var keys = list.GetProperty("keys").EnumerateArray().ToList();
        keys.Single(k => k.GetProperty("keyId").GetGuid() == oldId).GetProperty("active").GetBoolean().ShouldBeFalse();
        keys.Single(k => k.GetProperty("keyId").GetGuid() == rotated.GetProperty("keyId").GetGuid()).GetProperty("active").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Rotates_the_last_current_key_the_replacement_works_and_the_old_stops()
    {
        var seed = await SeedTenantAsync();
        await AuthProbeAsync(seed.Key, StatusCodes.Status200OK); // works + caches the original key

        var rotated = await RotateAsync(seed.Key, seed.KeyId); // rotating the ONLY key — allowed (a replacement is minted first)
        var newRaw = rotated.GetProperty("apiKey").GetString()!;

        await AuthProbeAsync(seed.Key, StatusCodes.Status401Unauthorized); // the rotated-out key stops immediately (cache invalidated)
        await AuthProbeAsync(newRaw, StatusCodes.Status200OK);             // the replacement is live
    }

    [Fact]
    public async Task Rotating_an_unknown_key_is_404()
    {
        var seed = await SeedTenantAsync();
        await RotateAsync(seed.Key, Guid.NewGuid(), StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Rotating_another_tenants_key_is_404_and_leaves_it_untouched()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await RotateAsync(a.Key, b.KeyId, StatusCodes.Status404NotFound); // right key id, wrong tenant — no existence oracle
        await AuthProbeAsync(b.Key, StatusCodes.Status200OK);             // B's key was never rotated out
    }

    [Fact]
    public async Task Rotating_an_already_revoked_key_is_404()
    {
        var seed = await SeedTenantAsync();
        var second = await MintAsync(seed.Key, new { });
        var secondId = second.GetProperty("keyId").GetGuid();
        await DeleteKeyAsync(seed.Key, secondId, StatusCodes.Status204NoContent);

        await RotateAsync(seed.Key, secondId, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Refuses_rotating_for_an_env_tenant()
    {
        var problem = await RotateAsync(TestTenants.PrimaryKey, Guid.NewGuid(), StatusCodes.Status400BadRequest);
        problem.GetProperty("title").GetString().ShouldBe("self_service_unavailable");
    }

    // ---- revoke --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revokes_a_non_current_key_and_it_stops_authenticating_immediately()
    {
        var seed = await SeedTenantAsync();
        var second = await MintAsync(seed.Key, new { });
        var secondRaw = second.GetProperty("apiKey").GetString()!;
        var secondId = second.GetProperty("keyId").GetGuid();

        await AuthProbeAsync(secondRaw, StatusCodes.Status200OK); // works + caches the second key
        await DeleteKeyAsync(seed.Key, secondId, StatusCodes.Status204NoContent); // revoke it with the FIRST key
        await AuthProbeAsync(secondRaw, StatusCodes.Status401Unauthorized); // revoked → rejected immediately (cache invalidated)
    }

    [Fact]
    public async Task Refuses_revoking_the_last_active_key()
    {
        var seed = await SeedTenantAsync();

        var problem = (await DeleteKeyAsync(seed.Key, seed.KeyId, StatusCodes.Status409Conflict)).Value;
        problem.GetProperty("title").GetString().ShouldBe("last_active_key");

        await AuthProbeAsync(seed.Key, StatusCodes.Status200OK); // still live — the refusal protected against lockout
    }

    [Fact]
    public async Task Refuses_revoking_the_key_authenticating_the_request()
    {
        var seed = await SeedTenantAsync();
        await MintAsync(seed.Key, new { }); // a second active key, so it's not the last-key guard that trips

        var problem = (await DeleteKeyAsync(seed.Key, seed.KeyId, StatusCodes.Status409Conflict)).Value; // revoking the CURRENT key
        problem.GetProperty("title").GetString().ShouldBe("current_key");

        await AuthProbeAsync(seed.Key, StatusCodes.Status200OK); // still live
    }

    [Fact]
    public async Task An_owner_membership_still_does_not_let_a_key_caller_revoke_its_own_last_key()
    {
        // Revoke-ALL exists for a tenant with a console recovery path — but the last key is the CURRENT key on a
        // key-authenticated request, and refuse-current is unchanged: rotate it, or use the console (which presents no key).
        var seed = await SeedTenantAsync();
        await Host.Services.GetRequiredService<ITenantMembershipStore>().CreateOwnerAsync(seed.TenantId, "owner@x.test", DateTimeOffset.UnixEpoch, _ct);

        var problem = (await DeleteKeyAsync(seed.Key, seed.KeyId, StatusCodes.Status409Conflict)).Value;
        problem.GetProperty("title").GetString().ShouldBe("current_key"); // NOT last_active_key — the owner membership waived that

        await AuthProbeAsync(seed.Key, StatusCodes.Status200OK); // still live
    }

    [Fact]
    public async Task Revokes_a_non_current_key_even_when_it_is_not_the_last_and_an_owner_membership_exists()
    {
        // The revoke-ALL rule never blocks a normal revoke: with an Owner membership present, a non-current, non-last key
        // revokes cleanly (this exercises the endpoint's allow-last path returning Revoked).
        var seed = await SeedTenantAsync();
        await Host.Services.GetRequiredService<ITenantMembershipStore>().CreateOwnerAsync(seed.TenantId, "owner@x.test", DateTimeOffset.UnixEpoch, _ct);
        var second = await MintAsync(seed.Key, new { });
        var secondId = second.GetProperty("keyId").GetGuid();

        await DeleteKeyAsync(seed.Key, secondId, StatusCodes.Status204NoContent); // revoke the non-current key with the first
    }

    [Fact]
    public async Task Revoking_an_unknown_key_is_404()
    {
        var seed = await SeedTenantAsync();
        await DeleteKeyAsync(seed.Key, Guid.NewGuid(), StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Revoking_an_already_revoked_key_is_404()
    {
        var seed = await SeedTenantAsync();
        await MintAsync(seed.Key, new { }); // keep >1 active so the target-revoked branch is what trips (not last-key)
        var third = await MintAsync(seed.Key, new { });
        var thirdId = third.GetProperty("keyId").GetGuid();

        await DeleteKeyAsync(seed.Key, thirdId, StatusCodes.Status204NoContent);
        await DeleteKeyAsync(seed.Key, thirdId, StatusCodes.Status404NotFound); // idempotent — the second revoke is a no-op 404
    }

    [Fact]
    public async Task Revoking_another_tenants_key_is_404_and_leaves_it_untouched()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await DeleteKeyAsync(a.Key, b.KeyId, StatusCodes.Status404NotFound); // right key id, wrong tenant
        await AuthProbeAsync(b.Key, StatusCodes.Status200OK);                // B's key was never revoked
    }

    [Fact]
    public async Task Refuses_revoking_for_an_env_tenant()
    {
        var problem = (await DeleteKeyAsync(TestTenants.PrimaryKey, Guid.NewGuid(), StatusCodes.Status400BadRequest)).Value;
        problem.GetProperty("title").GetString().ShouldBe("self_service_unavailable");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private sealed record Seed(string TenantId, string Key, Guid KeyId);

    private static string NewTenantId() => "t-" + Guid.NewGuid().ToString("N");

    // Seed a registry tenant (via the management surface) and return its first operator-issued key + that key's id.
    private async Task<Seed> SeedTenantAsync()
    {
        var id = NewTenantId();
        await ManagementPostAsync("/management/tenants", new { id, displayName = "Keys Tenant" }, StatusCodes.Status201Created);
        var issued = await ManagementPostAsync($"/management/tenants/{id}/keys", new { }, StatusCodes.Status201Created);
        return new Seed(id, issued.GetProperty("apiKey").GetString()!, issued.GetProperty("keyId").GetGuid());
    }

    private async Task<JsonElement> ManagementPostAsync(string url, object body, int expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Post.Json(body).ToUrl(url);
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ListAsync(string key, int expected = StatusCodes.Status200OK)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Get.Url("/tenant/keys");
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> MintAsync(string key, object body, int expected = StatusCodes.Status201Created)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Post.Json(body).ToUrl("/tenant/keys");
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> RotateAsync(string key, Guid keyId, int expected = StatusCodes.Status201Created)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Post.Json(new { }).ToUrl($"/tenant/keys/{keyId}/rotate"); // bodyless endpoint; the {} is ignored
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    // Returns the result so the caller can read a problem body (for 4xx) or ignore it (for 204).
    private async Task<(IScenarioResult Result, JsonElement Value)> DeleteKeyAsync(string key, Guid keyId, int expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Delete.Url($"/tenant/keys/{keyId}");
            x.StatusCodeShouldBe(expected);
        });

        // 204 has no body; a 4xx carries a problem document we may want to assert on.
        var value = expected == StatusCodes.Status204NoContent ? default : await result.ReadAsJsonAsync<JsonElement>();
        return (result, value);
    }

    private async Task AuthProbeAsync(string key, int expected) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Get.Url("/tenant"); // registry-aware profile read — the cheapest authenticated round-trip
            x.StatusCodeShouldBe(expected);
        });
}
