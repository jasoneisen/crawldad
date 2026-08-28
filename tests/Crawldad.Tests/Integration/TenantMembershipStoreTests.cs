using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The Marten-backed <see cref="ITenantMembershipStore"/> end to end (issue #119 PR4): create-owner is idempotent,
/// the two listings and the active lookup query correctly, and — the load-bearing invariant — a tenant's <b>last active
/// Owner</b> membership can never be revoked (the <c>409</c>-mapped <see cref="MembershipRevokeOutcome.LastOwner"/> guard),
/// so a workspace is never orphaned. Every email is expected already normalized by the caller.</summary>
[Collection(ManagementCollection.Name)]
public sealed class TenantMembershipStoreTests(ManagementFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private ITenantMembershipStore Store => fixture.Host.Services.GetRequiredService<ITenantMembershipStore>();

    public Task InitializeAsync() => fixture.Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewTenant() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Create_owner_is_idempotent_and_find_active_resolves_it()
    {
        var tenant = NewTenant();
        var created = await Store.CreateOwnerAsync(tenant, "a@x.test", DateTimeOffset.UnixEpoch, _ct);
        var again = await Store.CreateOwnerAsync(tenant, "a@x.test", DateTimeOffset.UnixEpoch, _ct);

        again.Id.ShouldBe(created.Id);                                   // same row — no duplicate
        created.Role.ShouldBe(MembershipRole.Owner);
        (await Store.ListForTenantAsync(tenant, _ct)).Count.ShouldBe(1); // only one membership exists

        var found = await Store.FindActiveAsync(tenant, "a@x.test", _ct);
        found!.Id.ShouldBe(created.Id);
        (await Store.FindActiveAsync(tenant, "nobody@x.test", _ct)).ShouldBeNull();
    }

    [Fact]
    public async Task List_for_email_returns_active_memberships_across_tenants_newest_first()
    {
        var first = NewTenant();
        var second = NewTenant();
        await Store.CreateOwnerAsync(first, "multi@x.test", DateTimeOffset.UnixEpoch, _ct);
        await Store.CreateOwnerAsync(second, "multi@x.test", DateTimeOffset.UnixEpoch.AddDays(1), _ct);

        var workspaces = await Store.ListForEmailAsync("multi@x.test", _ct);

        workspaces.Select(m => m.TenantId).ShouldBe([second, first]); // many-to-many: two workspaces, newest first
    }

    [Fact]
    public async Task Revoking_a_non_last_owner_succeeds_and_the_last_owner_is_refused()
    {
        var tenant = NewTenant();
        var owner1 = await Store.CreateOwnerAsync(tenant, "o1@x.test", DateTimeOffset.UnixEpoch, _ct);
        var owner2 = await Store.CreateOwnerAsync(tenant, "o2@x.test", DateTimeOffset.UnixEpoch, _ct);

        // Two active owners → revoking one is fine.
        (await Store.RevokeAsync(tenant, owner1.Id, DateTimeOffset.UnixEpoch, _ct)).ShouldBe(MembershipRevokeOutcome.Revoked);
        (await Store.FindActiveAsync(tenant, "o1@x.test", _ct)).ShouldBeNull();

        // Now o2 is the LAST active owner → revoking it is refused (the anti-orphan invariant → a 409).
        (await Store.RevokeAsync(tenant, owner2.Id, DateTimeOffset.UnixEpoch, _ct)).ShouldBe(MembershipRevokeOutcome.LastOwner);
        (await Store.FindActiveAsync(tenant, "o2@x.test", _ct)).ShouldNotBeNull(); // still active — nothing was written

        // ListForTenant still carries both (one revoked, one active).
        (await Store.ListForTenantAsync(tenant, _ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Revoking_a_non_owner_member_is_not_guarded_by_the_last_owner_rule()
    {
        var tenant = NewTenant();
        await Store.CreateOwnerAsync(tenant, "owner@x.test", DateTimeOffset.UnixEpoch, _ct); // the workspace keeps its owner
        var member = await SeedMemberAsync(tenant, "member@x.test");

        // A non-owner membership is never the "last owner", so it is revocable even though only one owner exists.
        (await Store.RevokeAsync(tenant, member, DateTimeOffset.UnixEpoch, _ct)).ShouldBe(MembershipRevokeOutcome.Revoked);
    }

    [Fact]
    public async Task Revoking_an_unknown_foreign_or_already_revoked_membership_is_not_found()
    {
        var tenant = NewTenant();
        var owner = await Store.CreateOwnerAsync(tenant, "o1@x.test", DateTimeOffset.UnixEpoch, _ct);
        await Store.CreateOwnerAsync(tenant, "o2@x.test", DateTimeOffset.UnixEpoch, _ct);

        (await Store.RevokeAsync(tenant, Guid.NewGuid(), DateTimeOffset.UnixEpoch, _ct)).ShouldBe(MembershipRevokeOutcome.NotFound); // unknown id
        (await Store.RevokeAsync("some-other-tenant", owner.Id, DateTimeOffset.UnixEpoch, _ct)).ShouldBe(MembershipRevokeOutcome.NotFound); // foreign tenant

        await Store.RevokeAsync(tenant, owner.Id, DateTimeOffset.UnixEpoch, _ct); // revoke it once (o2 keeps the workspace owned)
        (await Store.RevokeAsync(tenant, owner.Id, DateTimeOffset.UnixEpoch, _ct)).ShouldBe(MembershipRevokeOutcome.NotFound); // already revoked
    }

    // Seeds a non-owner Member membership directly (no member-creation flow exists yet), returning its id.
    private async Task<Guid> SeedMemberAsync(string tenantId, string email)
    {
        var id = Guid.NewGuid();
        await using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new TenantMembership
        {
            Id = id,
            TenantId = tenantId,
            Email = email,
            Role = MembershipRole.Member,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        await session.SaveChangesAsync(_ct);
        return id;
    }
}
