using System.Text.Json;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Integration;

/// <summary>Issue #119 PR1: <c>GET /tenant</c> and <c>GET /usage</c> are registry-aware. A tenant created via the
/// management API (i.e. everything signup creates) authenticates against the DB registry, not env config, so the old
/// <c>.First()</c> over <c>Crawldad:Tenants</c> threw a 500 for it — and that 500 broke the portal's workspace-link probe
/// (<see cref="WorkspaceLinker"/> validates a key by reading <c>GET /tenant</c>). These drive a real registry tenant
/// through the real pipeline on the management harness (#122's fixture): it reads its own profile and usage, and its key
/// links through the actual portal linker. All keys are synthetic.</summary>
[Collection(ManagementCollection.Name)]
public sealed class RegistryTenantReadTests(ManagementFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private RunLimitsOptions Limits => Host.Services.GetRequiredService<IOptions<RunLimitsOptions>>().Value;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync(); // isolate each test on the shared single-tenanted registry
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_registry_tenant_reads_its_own_profile_and_overrides_from_GET_tenant()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Reader Co", actor = "ops@reader", tier = "pro", slotAllowance = 4 });
        var raw = await IssueKeyAsync(id);

        var profile = await AuthenticatedGetJsonAsync("/tenant", raw);

        profile.GetProperty("tenantId").GetString().ShouldBe(id);
        profile.GetProperty("displayName").GetString().ShouldBe("Reader Co");      // the registry document's display name
        profile.GetProperty("tier").GetString().ShouldBe("pro");
        profile.GetProperty("slotAllowance").GetInt32().ShouldBe(4);               // the registry slot override
        profile.GetProperty("queueDepthAllowance").GetInt32().ShouldBe(Limits.MaxQueueDepthPerTenant); // registry has no depth field → global
    }

    [Fact]
    public async Task A_registry_tenant_reads_live_usage_with_its_slot_allowance_and_zeroed_tenant_scoped_numbers()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Usage Co", slotAllowance = 6 });
        var raw = await IssueKeyAsync(id);

        var usage = await AuthenticatedGetJsonAsync("/usage", raw);

        usage.GetProperty("slots").GetProperty("inUse").GetInt32().ShouldBe(0);
        usage.GetProperty("slots").GetProperty("allowance").GetInt32().ShouldBe(6); // the registry slot override, not the global default
        usage.GetProperty("queue").GetProperty("depth").GetInt32().ShouldBe(0);     // its own (empty) tenant partition
        usage.GetProperty("queue").GetProperty("sampled").GetInt32().ShouldBe(0);
        usage.GetProperty("runsStartedThisMonth").GetInt32().ShouldBe(0);
        usage.GetProperty("events").GetProperty("guardrail").GetInt32().ShouldBe(Limits.MaxEventsPerRun);
        usage.GetProperty("events").GetProperty("sampled").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_registry_tenants_key_claims_the_workspace_through_the_portal_probe_and_records_a_membership()
    {
        var id = NewTenantId();
        await CreateTenantAsync(new { id, displayName = "Claimable" }); // no overrides → the registry default path
        var raw = await IssueKeyAsync(id);

        // Drive the REAL portal WorkspaceLinker against the live API host: it validates a key by reading GET /tenant — the
        // exact probe that 500'd for a registry tenant before this fix — then, only on a valid, matching key, records the
        // account's Owner membership and DISCARDS the key (issue #119: no key is ever stored). A freshly provisioned registry
        // tenant must now claim.
        var linker = new WorkspaceLinker(new TestServerHttpClientFactory(Host));

        var result = await linker.LinkAsync("owner@example.com", id, raw);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Claimed);
        result.Message.ShouldNotContain(raw); // never echoes the key material

        // The Owner membership was recorded on the live API (the console authority) — that, not a stored key, is what a later
        // console read resolves against.
        var memberships = Host.Services.GetRequiredService<ITenantMembershipStore>();
        var membership = await memberships.FindActiveAsync(id, "owner@example.com", CancellationToken.None);
        membership.ShouldNotBeNull();
        membership.Role.ShouldBe(MembershipRole.Owner);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string NewTenantId() => "t-" + Guid.NewGuid().ToString("N");

    private async Task CreateTenantAsync(object body) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Post.Json(body).ToUrl("/management/tenants");
            x.StatusCodeShouldBe(StatusCodes.Status201Created);
        });

    private async Task<string> IssueKeyAsync(string id)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(ManagementFixture.ManagementKey));
            x.Post.Json(new { }).ToUrl($"/management/tenants/{id}/keys");
            x.StatusCodeShouldBe(StatusCodes.Status201Created);
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
    }

    private async Task<JsonElement> AuthenticatedGetJsonAsync(string url, string apiKey)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Get.Url(url);
            x.StatusCodeShouldBeOk();
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    // An IHttpClientFactory whose client targets the in-process API host — exactly what the portal's DI wires for the
    // real linker (a named client with the API base address preset), so the probe hits the live GET /tenant.
    private sealed class TestServerHttpClientFactory(IAlbaHost host) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => host.GetTestServer().CreateClient();
    }

}
