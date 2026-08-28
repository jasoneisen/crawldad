using System.Security.Claims;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace Crawldad.Tests.Unit;

/// <summary>The role-enforcement decision in isolation (issue #119 PR6): <see cref="ConsoleOwnerAuthorizationHandler"/>
/// admits the API-key channel unconditionally (key possession is full authority) and the console channel only for an
/// explicit Owner — fail-closed on a Member, an absent role, an unknown value, or conflicting roles. The channel is told
/// apart by the console identity's authentication type, never by the mere presence of a role claim, so a console principal
/// that somehow lost its role is denied rather than mistaken for a key.</summary>
public class ConsoleOwnerAuthorizationHandlerTests
{
    private static async Task<bool> AuthorizesAsync(ClaimsPrincipal user)
    {
        var requirement = new ConsoleOwnerRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new ConsoleOwnerAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    // A principal carrying an identity of the given authentication type with the given claims.
    private static ClaimsPrincipal Principal(string authType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authType));

    private static Claim Tenant => new(CrawldadClaims.TenantId, "7f2b8c40-1111-4a2b-9c3d-0123456789ab");
    private static Claim Actor => new(CrawldadClaims.Actor, "u@x.test");
    private static Claim Role(MembershipRole role) => new(CrawldadClaims.Role, role.ToString());

    [Fact]
    public async Task Api_key_principal_is_admitted_unconditionally()
    {
        // The API-key scheme stamps tenant + actor but never a role — its possession is full tenant authority.
        (await AuthorizesAsync(Principal(CrawldadAuthentication.Scheme, Tenant, Actor))).ShouldBeTrue();
    }

    [Fact]
    public async Task Console_owner_is_admitted()
    {
        (await AuthorizesAsync(Principal(ConsoleAuthModule.Scheme, Tenant, Actor, Role(MembershipRole.Owner)))).ShouldBeTrue();
    }

    [Fact]
    public async Task Console_member_is_denied()
    {
        (await AuthorizesAsync(Principal(ConsoleAuthModule.Scheme, Tenant, Actor, Role(MembershipRole.Member)))).ShouldBeFalse();
    }

    [Fact]
    public async Task Console_principal_without_a_role_is_denied()
    {
        // Fail-closed: a console identity that reached here without a role claim is a bug, not full authority.
        (await AuthorizesAsync(Principal(ConsoleAuthModule.Scheme, Tenant, Actor))).ShouldBeFalse();
    }

    [Fact]
    public async Task Console_principal_with_an_unknown_role_is_denied()
    {
        (await AuthorizesAsync(Principal(ConsoleAuthModule.Scheme, Tenant, Actor, new Claim(CrawldadClaims.Role, "Superuser")))).ShouldBeFalse();
    }

    [Fact]
    public async Task Console_principal_with_conflicting_roles_is_denied()
    {
        // Two distinct role claims are ambiguous — deny rather than pick one.
        (await AuthorizesAsync(Principal(ConsoleAuthModule.Scheme, Tenant, Actor, Role(MembershipRole.Owner), Role(MembershipRole.Member)))).ShouldBeFalse();
    }
}
