using System.Text.Json;
using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The console-read auth path end-to-end (issue #119 PR4), driven through the PR2 test-issuer harness on the
/// console-enabled host. A valid portal token whose selectors name an active membership reads that tenant's data; every
/// other shape is refused — no membership (<c>403</c>), a suspended or wrong workspace (<c>403</c>), both credentials at
/// once (<c>401</c>) — and an API-key request ignores the selectors entirely (they are a console-only mechanism).</summary>
[Collection(ConsoleAuthCollection.Name)]
public sealed class ConsoleReadPathTests(ConsoleAuthFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private static readonly CancellationToken _ct = CancellationToken.None;
    private const string _email = "owner@crawldad.test";

    public async Task InitializeAsync() => await Host.ResetAllMartenDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewTenantId() => Guid.NewGuid().ToString();

    private async Task SeedTenantAsync(string tenantId, TenantStatus status = TenantStatus.Active) =>
        await Host.Services.GetRequiredService<ITenantRegistryStore>().CreateAsync(new RegistryTenant
        {
            Id = tenantId,
            DisplayName = "Console Co",
            Actor = tenantId,
            Status = status,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, _ct);

    private async Task SeedMembershipAsync(string tenantId, string email) =>
        await Host.Services.GetRequiredService<ITenantMembershipStore>().CreateOwnerAsync(tenantId, email, DateTimeOffset.UnixEpoch, _ct);

    // A GET /tenant driven by a valid console token whose selectors name (email, workspace).
    private async Task<IScenarioResult> ConsoleGetTenantAsync(string email, string workspace, string? apiKey = null) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, email);
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, workspace);
            if (apiKey is not null)
            {
                x.WithRequestHeader(CrawldadAuthentication.ApiKeyHeader, apiKey);
            }

            x.Get.Url("/tenant");
            x.IgnoreStatusCode();
        });

    [Fact]
    public async Task Valid_token_with_a_membership_reads_the_tenants_data()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId);
        await SeedMembershipAsync(tenantId, _email);

        var result = await ConsoleGetTenantAsync(_email, tenantId);

        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("tenantId").GetString().ShouldBe(tenantId);
    }

    [Fact]
    public async Task A_valid_token_with_no_membership_is_forbidden()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId); // tenant exists, but this user is not a member

        var result = await ConsoleGetTenantAsync(_email, tenantId);

        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_membership_for_a_different_workspace_is_forbidden()
    {
        var mine = NewTenantId();
        var other = NewTenantId();
        await SeedTenantAsync(mine);
        await SeedTenantAsync(other);
        await SeedMembershipAsync(mine, _email); // member of `mine`, not `other`

        var result = await ConsoleGetTenantAsync(_email, other); // selector names the workspace the user does NOT belong to

        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_membership_on_a_suspended_tenant_is_forbidden()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId, TenantStatus.Suspended);
        await SeedMembershipAsync(tenantId, _email); // membership exists, but the tenant is suspended

        var result = await ConsoleGetTenantAsync(_email, tenantId);

        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Presenting_both_a_console_token_and_an_api_key_is_rejected()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId);
        await SeedMembershipAsync(tenantId, _email);

        // A valid console token AND an API key on the same request is ambiguous — rejected (401), never merged.
        var result = await ConsoleGetTenantAsync(_email, tenantId, apiKey: TestTenants.PrimaryKey);

        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task The_selector_headers_are_ignored_under_api_key_auth()
    {
        var registryTenant = NewTenantId();
        await SeedTenantAsync(registryTenant);
        await SeedMembershipAsync(registryTenant, _email);

        // An API-key request that ALSO carries the console selectors resolves to the KEY's tenant (the env primary), never
        // the selector's registry tenant — the selectors are a console-only mechanism the ApiKey scheme never reads.
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader(CrawldadAuthentication.ApiKeyHeader, TestTenants.PrimaryKey);
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, _email);
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, registryTenant);
            x.Get.Url("/tenant");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("tenantId").GetString().ShouldBe(TestTenants.PrimaryId);
    }

    [Fact]
    public async Task A_membership_pointing_at_an_unknown_tenant_is_forbidden()
    {
        var tenantId = NewTenantId();
        await SeedMembershipAsync(tenantId, _email); // a membership, but NO registry tenant behind it

        var result = await ConsoleGetTenantAsync(_email, tenantId);

        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_workspace_selector_with_no_user_selector_is_forbidden()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId);
        await SeedMembershipAsync(tenantId, _email);

        // Only the workspace selector is present — with no user to resolve, there is no membership to stamp → 403.
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, tenantId);
            x.Get.Url("/tenant");
            x.IgnoreStatusCode();
        });

        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_valid_token_with_no_selectors_is_forbidden()
    {
        // A portal token that proves the portal but carries no (user, workspace) selectors resolves to no tenant claim →
        // the ConsoleOrKey policy denies it as a 403.
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.Get.Url("/tenant");
            x.IgnoreStatusCode();
        });

        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }
}
