using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Tenant self-service membership management — <c>/tenant/memberships</c>, authenticated by the tenant's <b>own</b>
/// key (the normal ApiKey scheme; a tenant acting on itself, no management credential). These write and read the console
/// authorization store (<see cref="ITenantMembershipStore"/>): the portal's attach flow, having proved possession of the
/// tenant key, records the signed-in user's <see cref="MembershipRole.Owner"/> membership so subsequent <b>console</b>
/// reads for that verified email resolve to this tenant. Recording a membership grants no authority the tenant key does not
/// already hold — the key holder is authorizing an email it chose — so it is a self-service write like <c>/tenant/keys</c>.
///
/// <para><b>Registry tenants only</b> (parity with <c>/tenant/keys</c>): an env-configured tenant has no console surface, so
/// the endpoints are a clear <c>400</c> (<see cref="MembershipProblems.SelfServiceUnavailable"/>) for it. This surface is
/// <b>not</b> console-reachable — it is the key path that bootstraps the very membership a console request needs.</para></summary>
public static class MembershipEndpoints
{
    /// <summary>Handles <c>GET /tenant/memberships</c>: the tenant's memberships, newest first — metadata only.</summary>
    [WolverineGet("/tenant/memberships")]
    public static async Task<IResult> List(
        TenantContext tenant,
        ITenantRegistryStore registry,
        ITenantMembershipStore memberships,
        CancellationToken ct)
    {
        if (await registry.FindAsync(tenant.TenantId, ct) is null)
        {
            return MembershipProblems.SelfServiceUnavailable();
        }

        var rows = await memberships.ListForTenantAsync(tenant.TenantId, ct);
        return Results.Ok(new TenantMembershipList([.. rows.Select(ToInfo)]));
    }

    /// <summary>Handles <c>POST /tenant/memberships</c>: record (idempotently) an <see cref="MembershipRole.Owner"/>
    /// membership for the request's verified email in the caller's tenant. Returns the membership.</summary>
    [WolverinePost("/tenant/memberships")]
    public static async Task<IResult> Record(
        RecordMembershipRequest request,
        TenantContext tenant,
        ITenantRegistryStore registry,
        ITenantMembershipStore memberships,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await registry.FindAsync(tenant.TenantId, ct) is null)
        {
            return MembershipProblems.SelfServiceUnavailable();
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return MembershipProblems.InvalidEmail("a member email is required");
        }

        var email = EmailAddress.Normalize(request.Email);
        var membership = await memberships.CreateOwnerAsync(tenant.TenantId, email, clock.GetUtcNow(), ct);
        return Results.Ok(ToInfo(membership));
    }

    // Projects a stored membership to its metadata-only listing row.
    private static TenantMembershipInfo ToInfo(TenantMembership membership) =>
        new(membership.Id, membership.Email, membership.Role, membership.CreatedAt, membership.RevokedAt, membership.RevokedAt is null);
}
