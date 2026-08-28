using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Tenancy;

/// <summary>A portal user's role in a workspace (tenant). The console membership store maps a verified email to a tenant
/// through one of these; the role gates future member-management (invites/removal), and the <see cref="Owner"/> role is
/// the one the workspace can never be left without.</summary>
public enum MembershipRole
{
    /// <summary>Full control of the workspace — the signup creator, and the role a self-service attach records. A tenant's
    /// last active <see cref="Owner"/> membership can never be removed or downgraded (the anti-orphan invariant).</summary>
    Owner,

    /// <summary>A non-owner member (a future invitee). Reserved for later member-management; the attach flow records
    /// <see cref="Owner"/> today.</summary>
    Member,
}

/// <summary>The <c>POST /tenant/memberships</c> request body: record (idempotently) an <see cref="MembershipRole.Owner"/>
/// membership for <paramref name="Email"/> in the authenticated tenant. Called by the portal's attach flow after it has
/// proved possession of the tenant key, so a subsequent console read for that verified email resolves to this tenant.</summary>
/// <param name="Email">The verified portal email to grant the workspace to (normalized server-side).</param>
public sealed record RecordMembershipRequest(string Email);

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
