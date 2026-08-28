using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The <see cref="TenantWriteLock"/> under REAL parallelism (issue #119 PR6, PR#154 forward item). The shipped guard
/// tests are sequential — they would stay green even if the advisory lock were a no-op — so this fires genuinely-parallel,
/// barrier-aligned writes and asserts the count-based invariants hold under contention: two racing revokes of a tenant's last
/// two Owners (or last two keys) must end with <b>exactly one</b> success and one refusal, and two racing attaches of the same
/// <c>(tenant, email)</c> must produce <b>exactly one</b> active membership. Each is run over several rounds so a missing lock
/// fails deterministically, not just occasionally.</summary>
[Collection(ManagementCollection.Name)]
public sealed class TenantWriteLockConcurrencyTests(ManagementFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private const int _rounds = 8;

    private ITenantMembershipStore Memberships => fixture.Host.Services.GetRequiredService<ITenantMembershipStore>();
    private ITenantRegistryStore Registry => fixture.Host.Services.GetRequiredService<ITenantRegistryStore>();

    public Task InitializeAsync() => fixture.Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Parallel_revoke_of_the_last_two_owners_leaves_exactly_one_owner()
    {
        for (var round = 0; round < _rounds; round++)
        {
            var tenant = Guid.NewGuid().ToString();
            var owner1 = await Memberships.CreateOwnerAsync(tenant, "o1@x.test", DateTimeOffset.UnixEpoch, _ct);
            var owner2 = await Memberships.CreateOwnerAsync(tenant, "o2@x.test", DateTimeOffset.UnixEpoch, _ct);

            var outcomes = await RaceAsync(
                () => Memberships.RevokeAsync(tenant, owner1.Id, DateTimeOffset.UnixEpoch, _ct),
                () => Memberships.RevokeAsync(tenant, owner2.Id, DateTimeOffset.UnixEpoch, _ct));

            outcomes.Count(o => o == MembershipRevokeOutcome.Revoked).ShouldBe(1, "exactly one revoke may win");
            outcomes.Count(o => o == MembershipRevokeOutcome.LastOwner).ShouldBe(1, "the other must be refused as the last Owner");
            (await Memberships.HasActiveOwnerAsync(tenant, _ct)).ShouldBeTrue("an Owner always remains");
        }
    }

    [Fact]
    public async Task Parallel_revoke_of_the_last_two_keys_leaves_exactly_one_active_key()
    {
        for (var round = 0; round < _rounds; round++)
        {
            var tenant = Guid.NewGuid().ToString();
            await Registry.CreateAsync(NewTenant(tenant), _ct);
            var key1 = await AddKeyAsync(tenant);
            var key2 = await AddKeyAsync(tenant);

            // allowLast:false and a non-matching presented hash, so the ONLY refusal in play is the last-active-key guard.
            var outcomes = await RaceAsync(
                () => Registry.RevokeKeyGuardedAsync(tenant, key1, "no-match", allowLastActive: false, DateTimeOffset.UnixEpoch, _ct),
                () => Registry.RevokeKeyGuardedAsync(tenant, key2, "no-match", allowLastActive: false, DateTimeOffset.UnixEpoch, _ct));

            outcomes.Count(o => o == KeyRevokeOutcome.Revoked).ShouldBe(1, "exactly one revoke may win");
            outcomes.Count(o => o == KeyRevokeOutcome.LastActive).ShouldBe(1, "the other must be refused as the last active key");
            (await Registry.ListKeysAsync(tenant, _ct)).Count(k => k.RevokedAt is null).ShouldBe(1, "one active key always remains");
        }
    }

    [Fact]
    public async Task Parallel_attach_of_the_same_pair_produces_exactly_one_active_membership()
    {
        for (var round = 0; round < _rounds; round++)
        {
            var tenant = Guid.NewGuid().ToString();

            var results = await RaceAsync(
                () => Memberships.CreateOwnerAsync(tenant, "dup@x.test", DateTimeOffset.UnixEpoch, _ct),
                () => Memberships.CreateOwnerAsync(tenant, "dup@x.test", DateTimeOffset.UnixEpoch, _ct));

            results[0].Id.ShouldBe(results[1].Id, "both racing attaches must resolve to the same membership row");
            var active = (await Memberships.ListForTenantAsync(tenant, _ct)).Count(m => m.RevokedAt is null);
            active.ShouldBe(1, "the active (tenant, email) pair is unique — never duplicated by a concurrent attach");
        }
    }

    // Runs two operations genuinely in parallel, aligned on a barrier so both are in flight before either can commit.
    private static async Task<T[]> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        using var barrier = new Barrier(2);
        var a = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await first();
        });
        var b = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await second();
        });
        return await Task.WhenAll(a, b);
    }

    private static RegistryTenant NewTenant(string id) => new()
    {
        Id = id,
        DisplayName = "Lock Race",
        Actor = id,
        Status = TenantStatus.Active,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private async Task<Guid> AddKeyAsync(string tenantId)
    {
        var id = Guid.NewGuid();
        await Registry.AddKeyAsync(new TenantApiKey
        {
            Id = id,
            TenantId = tenantId,
            KeyHash = "hash-" + id.ToString("N"),
            Prefix = "ck_test_" + id.ToString("N")[..6],
            CreatedAt = DateTimeOffset.UnixEpoch,
        }, _ct);
        return id;
    }
}
