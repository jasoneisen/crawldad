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
}
