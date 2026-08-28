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
