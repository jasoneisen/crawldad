using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Tests.Unit;

/// <summary>The stored-enum ordinal contract. This repo sets no Marten serializer override, so Marten's default
/// <c>EnumStorage.AsInteger</c> is in force and every enum member reaches JSONB as its <b>integer</b> — in documents
/// (<c>RunProgress</c>, <c>RegistryTenant</c>, <c>TenantMembership</c>), in snapshots (<c>Run</c>, <c>Payload</c>), in
/// async projection views (<c>RunTimeline</c>, <c>RunSummary</c>, <c>PayloadSummary</c>), in the computed index over
/// <c>RunSummary.Status</c>, and in the LINQ predicates translated against it. The HTTP wire disagrees by design (the
/// <c>JsonStringEnumConverter</c> in <c>ContractsJson</c> sends camelCase names), and it is the store's numbers that are
/// durable — so reordering a member silently re-maps every row already written.
/// <para>Deliberately <b>not</b> an <c>Enum.GetValues</c> walk: a renumber keeps every member's name, so a
/// name-driven walk passes straight through the exact mistake this guards. Each member is pinned by hand to the
/// literal integer it must keep forever, and a member-count guard per enum makes a NEW member fail here until it is
/// pinned too — appending is allowed, editing an existing pin is not.</para></summary>
public class EnumOrdinalContractTests
{
    [Theory]
    [InlineData(RunStatus.Running, 0)]
    [InlineData(RunStatus.Succeeded, 1)]
    [InlineData(RunStatus.Failed, 2)]
    [InlineData(RunStatus.Cancelled, 3)]
    [InlineData(RunStatus.Queued, 4)]
    public void RunStatus_ordinals_are_the_stored_contract(RunStatus member, int ordinal) =>
        ((int)member).ShouldBe(ordinal);

    [Theory]
    [InlineData(RunLifecycle.Running, 0)]
    [InlineData(RunLifecycle.Succeeded, 1)]
    [InlineData(RunLifecycle.Failed, 2)]
    [InlineData(RunLifecycle.Cancelled, 3)]
    [InlineData(RunLifecycle.Queued, 4)]
    public void RunLifecycle_ordinals_are_the_stored_contract(RunLifecycle member, int ordinal) =>
        ((int)member).ShouldBe(ordinal);

    [Theory]
    [InlineData(PayloadStatus.Active, 0)]
    [InlineData(PayloadStatus.Archived, 1)]
    public void PayloadStatus_ordinals_are_the_stored_contract(PayloadStatus member, int ordinal) =>
        ((int)member).ShouldBe(ordinal);

    [Theory]
    [InlineData(TenantStatus.Active, 0)]
    [InlineData(TenantStatus.Suspended, 1)]
    public void TenantStatus_ordinals_are_the_stored_contract(TenantStatus member, int ordinal) =>
        ((int)member).ShouldBe(ordinal);

    [Theory]
    [InlineData(MembershipRole.Owner, 0)]
    [InlineData(MembershipRole.Member, 1)]
    public void MembershipRole_ordinals_are_the_stored_contract(MembershipRole member, int ordinal) =>
        ((int)member).ShouldBe(ordinal);

    /// <summary>The completeness half of the contract: a member added to a stored enum without a pin above fails here.
    /// Fix it by APPENDING an <c>[InlineData]</c> for the new member with the next free value and bumping the count —
    /// never by renumbering an existing one.</summary>
    [Theory]
    [InlineData(typeof(RunStatus), 5)]
    [InlineData(typeof(RunLifecycle), 5)]
    [InlineData(typeof(PayloadStatus), 2)]
    [InlineData(typeof(TenantStatus), 2)]
    [InlineData(typeof(MembershipRole), 2)]
    public void Every_member_of_a_stored_enum_is_pinned(Type storedEnum, int pinnedMembers) =>
        Enum.GetValues(storedEnum).Length.ShouldBe(pinnedMembers);
}
