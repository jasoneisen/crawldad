using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Tests.Client;

/// <summary>The SDK's free-tier provisioning call (issue #119 PR7): <c>ProvisionTenantAsync</c> posts to
/// <c>provisioning/tenants</c> and returns the created <see cref="WorkspaceSummary"/> on 201, maps the one-per-email 409 to a
/// <see cref="CrawldadApiException"/> whose body still carries the existing workspace, and maps a 401 (e.g. an API-key client)
/// to <see cref="CrawldadUnauthorizedException"/>.</summary>
public class CrawldadClientProvisioningTests
{
    [Fact]
    public async Task Provision_posts_the_optional_display_name_and_returns_the_created_workspace()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceSummary("t-new", "Acme", MembershipRole.Owner), HttpStatusCode.Created));
        var client = ClientTestHarness.ClientFor(handler);

        var workspace = await client.ProvisionTenantAsync("Acme");

        workspace.TenantId.ShouldBe("t-new");
        workspace.DisplayName.ShouldBe("Acme");
        workspace.Role.ShouldBe(MembershipRole.Owner);
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/provisioning/tenants");
        handler.Last.Body.ShouldContain("Acme");
    }

    [Fact]
    public async Task Provision_with_no_display_name_sends_a_null_field()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceSummary("t-new", "My workspace", MembershipRole.Owner), HttpStatusCode.Created));
        var client = ClientTestHarness.ClientFor(handler);

        await client.ProvisionTenantAsync();

        handler.Last.Body.ShouldContain("displayName"); // the field is present (null) — the endpoint defaults it
    }

    [Fact]
    public async Task An_already_provisioned_409_surfaces_the_existing_workspace_in_the_body()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.JsonRaw(HttpStatusCode.Conflict, """{"title":"free_tenant_exists","status":409,"tenantId":"t-existing"}"""));
        var client = ClientTestHarness.ClientFor(handler);

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.ProvisionTenantAsync());

        ex.StatusCode.ShouldBe(409);
        ex.ResponseBody.ShouldNotBeNull().ShouldContain("t-existing"); // the portal parses this to recover the link
    }

    [Fact]
    public async Task A_401_maps_to_the_unauthorized_exception()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.Unauthorized));
        var client = ClientTestHarness.ClientFor(handler);

        await Should.ThrowAsync<CrawldadUnauthorizedException>(() => client.ProvisionTenantAsync());
    }
}
