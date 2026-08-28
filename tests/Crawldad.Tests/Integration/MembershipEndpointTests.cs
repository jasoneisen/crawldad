using System.Text.Json;
using Alba;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Integration;

/// <summary>Tenant self-service membership endpoints (<c>/tenant/memberships</c>) end to end, authenticated by the tenant's
/// OWN key (issue #119 PR4). The portal's attach flow records an owner membership here; the account area lists it. Records
/// are idempotent, the email is required and normalized, and — like <c>/tenant/keys</c> — the surface is registry-tenants
/// only (an env tenant is a 400). Reuses the management surface only to seed a registry tenant + its first key.</summary>
[Collection(ManagementCollection.Name)]
public sealed class MembershipEndpointTests(ManagementFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Records_an_owner_membership_idempotently_and_lists_it()
    {
        var seed = await SeedTenantAsync();

        var recorded = await RecordAsync(seed.Key, "Owner@Example.com", StatusCodes.Status200OK);
        recorded.GetProperty("email").GetString().ShouldBe("owner@example.com"); // normalized server-side
        recorded.GetProperty("role").GetString().ShouldBe("owner");
        recorded.GetProperty("active").GetBoolean().ShouldBeTrue();

        // Re-record the same email → the same membership, no duplicate.
        var again = await RecordAsync(seed.Key, "owner@example.com", StatusCodes.Status200OK);
        again.GetProperty("membershipId").GetGuid().ShouldBe(recorded.GetProperty("membershipId").GetGuid());

        var list = await ListAsync(seed.Key);
        list.GetProperty("memberships").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_missing_email_is_a_validation_problem()
    {
        var seed = await SeedTenantAsync();

        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(seed.Key));
            x.Post.Json(new { email = "" }).ToUrl("/tenant/memberships");
            x.StatusCodeShouldBe(StatusCodes.Status400BadRequest);
        });
    }

    [Fact]
    public async Task An_env_tenant_cannot_use_the_membership_surface()
    {
        // The env-configured primary tenant has no RegistryTenant doc → self-service is unavailable (a 400), just like keys.
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Post.Json(new { email = "x@example.com" }).ToUrl("/tenant/memberships");
            x.StatusCodeShouldBe(StatusCodes.Status400BadRequest);
        });

        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Get.Url("/tenant/memberships");
            x.StatusCodeShouldBe(StatusCodes.Status400BadRequest);
        });
    }

    [Fact]
    public async Task Owner_key_adds_a_member_changes_its_role_and_removes_it()
    {
        var seed = await SeedTenantAsync();
        await RecordAsync(seed.Key, "owner@example.com", StatusCodes.Status200OK); // the workspace keeps an Owner throughout

        // Add a member with an explicit role (key channel is unrestricted — no console Owner gate).
        var added = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(seed.Key));
            x.Post.Json(new { email = "Member@Example.com", role = "member" }).ToUrl("/tenant/memberships");
            x.StatusCodeShouldBe(StatusCodes.Status200OK);
        });
        var member = await added.ReadAsJsonAsync<JsonElement>();
        member.GetProperty("role").GetString().ShouldBe("member");
        member.GetProperty("email").GetString().ShouldBe("member@example.com"); // normalized
        var memberId = member.GetProperty("membershipId").GetGuid();

        // Change its role to owner, then back to member.
        var promoted = await ChangeRoleAsync(seed.Key, memberId, "owner", StatusCodes.Status200OK);
        promoted.GetProperty("role").GetString().ShouldBe("owner");
        await ChangeRoleAsync(seed.Key, memberId, "member", StatusCodes.Status200OK);

        // Remove it (idempotent — a second remove is a 404).
        await RemoveAsync(seed.Key, memberId, StatusCodes.Status204NoContent);
        await RemoveAsync(seed.Key, memberId, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task The_last_owner_can_neither_be_removed_nor_downgraded()
    {
        var seed = await SeedTenantAsync();
        var owner = await RecordAsync(seed.Key, "solo@example.com", StatusCodes.Status200OK);
        var ownerId = owner.GetProperty("membershipId").GetGuid();

        await RemoveAsync(seed.Key, ownerId, StatusCodes.Status409Conflict);              // last Owner — refused
        await ChangeRoleAsync(seed.Key, ownerId, "member", StatusCodes.Status409Conflict); // downgrade of the last Owner — refused
    }

    [Fact]
    public async Task Removing_or_changing_an_unknown_membership_is_a_404()
    {
        var seed = await SeedTenantAsync();
        await RecordAsync(seed.Key, "owner@example.com", StatusCodes.Status200OK);

        await RemoveAsync(seed.Key, Guid.NewGuid(), StatusCodes.Status404NotFound);
        await ChangeRoleAsync(seed.Key, Guid.NewGuid(), "owner", StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task An_env_tenant_cannot_use_the_management_endpoints()
    {
        await RemoveAsync(TestTenants.PrimaryKey, Guid.NewGuid(), StatusCodes.Status400BadRequest);
        await ChangeRoleAsync(TestTenants.PrimaryKey, Guid.NewGuid(), "owner", StatusCodes.Status400BadRequest);
    }

    private async Task<JsonElement> RecordAsync(string key, string email, int expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Post.Json(new { email }).ToUrl("/tenant/memberships");
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task RemoveAsync(string key, Guid membershipId, int expected) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Delete.Url($"/tenant/memberships/{membershipId}");
            x.StatusCodeShouldBe(expected);
        });

    private async Task<JsonElement> ChangeRoleAsync(string key, Guid membershipId, string role, int expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Post.Json(new { role }).ToUrl($"/tenant/memberships/{membershipId}/role");
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ListAsync(string key)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(key));
            x.Get.Url("/tenant/memberships");
            x.StatusCodeShouldBeOk();
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private sealed record Seed(string Id, string Key);

    private async Task<Seed> SeedTenantAsync()
    {
        var id = "m-" + Guid.NewGuid().ToString("N");
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Post.Json(new { id, displayName = "Members Co" }).ToUrl("/management/tenants");
            x.StatusCodeShouldBe(StatusCodes.Status201Created);
        });

        var issued = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Post.Json(new { }).ToUrl($"/management/tenants/{id}/keys");
            x.StatusCodeShouldBe(StatusCodes.Status201Created);
        });
        var key = (await issued.ReadAsJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        return new Seed(id, key);
    }
}
