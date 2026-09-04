using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Tenancy;

/// <summary>A portal user's role in a workspace (tenant). The console membership store maps a verified email to a tenant
/// through one of these; the role gates future member-management (invites/removal), and the <see cref="Owner"/> role is
/// the one the workspace can never be left without.
/// <para><b>Stored as its ordinal.</b> The wire is camelCase names, but the store is not: with no Marten serializer
/// override the default <c>EnumStorage.AsInteger</c> is in force, so the integers below — not the names — are what sits
/// in the <c>TenantMembership</c> document the anti-orphan Owner invariant is evaluated over. The explicit values are an
/// append-only contract: add a member with the next free value, <b>never renumber</b>, and retire one as a tombstone
/// that keeps its value. Pinned member-by-member in <c>EnumOrdinalContractTests</c>.</para></summary>
public enum MembershipRole
{
    /// <summary>Full control of the workspace — the signup creator, and the role a self-service attach records. A tenant's
    /// last active <see cref="Owner"/> membership can never be removed or downgraded (the anti-orphan invariant).</summary>
    Owner = 0,

    /// <summary>A non-owner member (a future invitee). Reserved for later member-management; the attach flow records
    /// <see cref="Owner"/> today.</summary>
    Member = 1,
}

/// <summary>The <c>POST /tenant/memberships</c> request body: record (idempotently) a membership for
/// <paramref name="Email"/> in the authenticated tenant. Two callers: the portal's attach flow (after it has proved
/// possession of the tenant key) records the signed-in user with the default <see cref="MembershipRole.Owner"/> role
/// (<paramref name="Role"/> omitted); an Owner adding a teammate passes an explicit <paramref name="Role"/> (typically
/// <see cref="MembershipRole.Member"/>). A re-record of an already-active <c>(tenant, email)</c> returns the existing
/// membership unchanged — its role is not altered by a second record (use <see cref="ChangeMembershipRoleRequest"/>).</summary>
/// <param name="Email">The verified portal email to grant the workspace to (normalized server-side).</param>
/// <param name="Role">The role to record, or null to default to <see cref="MembershipRole.Owner"/> (the attach flow's
/// self-owner). An explicit value is how an Owner adds a <see cref="MembershipRole.Member"/>.</param>
public sealed record RecordMembershipRequest(string Email, MembershipRole? Role = null);

/// <summary>The <c>POST /tenant/memberships/{id}/role</c> request body: set an existing membership's
/// <paramref name="Role"/>. Owner-only. Downgrading the tenant's <b>last active <see cref="MembershipRole.Owner"/></b> is
/// refused (the anti-orphan invariant) — a workspace must always keep at least one Owner.</summary>
/// <param name="Role">The role to set the membership to.</param>
public sealed record ChangeMembershipRoleRequest(MembershipRole Role);

/// <summary>One of a tenant's memberships, as returned by <c>GET /tenant/memberships</c> and the record response —
/// metadata only (the email is portal identity, never a credential).</summary>
/// <param name="MembershipId">The membership record id.</param>
/// <param name="Email">The member's normalized email.</param>
/// <param name="Role">The member's role in this workspace.</param>
/// <param name="CreatedAt">When the membership was recorded (UTC).</param>
/// <param name="RevokedAt">When the membership was revoked, or null while it is active.</param>
/// <param name="Active">Whether the membership is currently active (not revoked).</param>
public sealed record TenantMembershipInfo(
    Guid MembershipId,
    string Email,
    MembershipRole Role,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RevokedAt,
    bool Active);

/// <summary>The <c>GET /tenant/memberships</c> response: the authenticated tenant's memberships, newest first.</summary>
/// <param name="Memberships">The tenant's membership rows.</param>
public sealed record TenantMembershipList(IReadOnlyList<TenantMembershipInfo> Memberships);
