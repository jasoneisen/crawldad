using System.Net;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Tests.Client;

/// <summary>The SDK membership surface (issue #119 PR4): the portal's attach flow records an owner membership and the
/// account area lists them, both through the tenant's own client. Thin one-endpoint mappings, verified against the stub.</summary>
public class CrawldadClientMembershipsTests
{
    [Fact]
    public async Task Records_an_owner_membership()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new TenantMembershipInfo(Guid.NewGuid(), "u@x.test", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true)));
        var client = ClientTestHarness.ClientFor(handler);

        var info = await client.RecordOwnerMembershipAsync("u@x.test");

        info.Email.ShouldBe("u@x.test");
        info.Role.ShouldBe(MembershipRole.Owner);
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/tenant/memberships");
        handler.Last.Body.ShouldContain("u@x.test");
    }

    [Fact]
    public async Task Lists_memberships()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new TenantMembershipList([new(Guid.NewGuid(), "u@x.test", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true)])));
        var client = ClientTestHarness.ClientFor(handler);

        var list = await client.ListMembershipsAsync();

        list.Memberships.Count.ShouldBe(1);
        handler.Last.Method.ShouldBe(HttpMethod.Get);
        handler.Last.Path.ShouldBe("/tenant/memberships");
    }

    [Fact]
    public async Task Adds_a_member_with_a_role()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new TenantMembershipInfo(Guid.NewGuid(), "m@x.test", MembershipRole.Member, DateTimeOffset.UnixEpoch, null, true)));
        var client = ClientTestHarness.ClientFor(handler);

        var info = await client.AddMembershipAsync("m@x.test", MembershipRole.Member);

        info.Role.ShouldBe(MembershipRole.Member);
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/tenant/memberships");
        handler.Last.Body.ShouldContain("member"); // the role rides in the body
    }

    [Fact]
    public async Task Removes_a_membership()
    {
        var id = Guid.NewGuid();
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.NoContent));
        var client = ClientTestHarness.ClientFor(handler);

        await client.RemoveMembershipAsync(id);

        handler.Last.Method.ShouldBe(HttpMethod.Delete);
        handler.Last.Path.ShouldBe($"/tenant/memberships/{id}");
    }

    [Fact]
    public async Task Changes_a_membership_role()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new TenantMembershipInfo(id, "m@x.test", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true)));
        var client = ClientTestHarness.ClientFor(handler);

        var info = await client.ChangeMembershipRoleAsync(id, MembershipRole.Owner);

        info.Role.ShouldBe(MembershipRole.Owner);
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe($"/tenant/memberships/{id}/role");
        handler.Last.Body.ShouldContain("owner");
    }

    [Fact]
    public async Task Lists_the_callers_workspaces()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceList([new("t-1", "Acme", MembershipRole.Owner), new("t-2", "Beta", MembershipRole.Member)])));
        var client = ClientTestHarness.ClientFor(handler);

        var workspaces = await client.ListMyWorkspacesAsync();

        workspaces.Workspaces.Count.ShouldBe(2);
        workspaces.Workspaces[0].DisplayName.ShouldBe("Acme");
        workspaces.Workspaces[1].Role.ShouldBe(MembershipRole.Member);
        handler.Last.Method.ShouldBe(HttpMethod.Get);
        handler.Last.Path.ShouldBe("/workspaces");
    }
}
