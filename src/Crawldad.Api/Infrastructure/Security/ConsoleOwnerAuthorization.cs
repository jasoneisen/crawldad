using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The Owner-only authorization requirement for the <see cref="ConsoleAuthModule.ConsoleOwnerOrKeyPolicy"/> (issue
/// #119 PR6). It draws the one line role enforcement needs: on the <b>console</b> channel a principal must be an
/// <see cref="MembershipRole.Owner"/> to reach key/membership management; on the <b>API-key</b> channel there is no role
/// gate at all (key possession has always been full tenant authority). The <see cref="ConsoleOwnerAuthorizationHandler"/>
/// makes that call, fail-closed on the console side — a Member, an absent role, or any unrecognized/ambiguous role is
/// denied (a <c>403</c>, since the request is authenticated).</summary>
public sealed class ConsoleOwnerRequirement : IAuthorizationRequirement;

/// <summary>Authorizes <see cref="ConsoleOwnerRequirement"/>. It distinguishes the two channels by the
/// <b>authentication type</b> of the stamped identity — the same signal <see cref="ConsoleWriteAuditMiddleware"/> uses — so
/// the decision never rests on the mere presence/absence of a role claim (which would fail <i>open</i> if a console
/// principal ever reached here without one):
/// <list type="bullet">
/// <item><b>No console identity</b> ⇒ the request was authenticated by the API-key scheme (the base policy already required
/// an authenticated tenant). Key authority is unrestricted, so the requirement is satisfied.</item>
/// <item><b>A console identity</b> ⇒ the principal must carry exactly the <c>Owner</c> role. A <c>Member</c>, a missing
/// role, an unknown value, or conflicting role claims all leave the requirement unmet ⇒ <c>403</c>.</item>
/// </list></summary>
internal sealed class ConsoleOwnerAuthorizationHandler : AuthorizationHandler<ConsoleOwnerRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ConsoleOwnerRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var consoleAuthenticated = context.User.Identities.Any(identity =>
            string.Equals(identity.AuthenticationType, ConsoleAuthModule.Scheme, StringComparison.Ordinal));
        if (!consoleAuthenticated)
        {
            context.Succeed(requirement); // API-key channel — full tenant authority, no role gate
            return Task.CompletedTask;
        }

        // Console channel — require an explicit, unambiguous Owner role. Anything else (Member, missing, unknown, or
        // multiple distinct values) is denied: fail-closed, only explicit Owner is accepted.
        var roles = context.User.FindAll(CrawldadClaims.Role)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (roles is [var only] && string.Equals(only, nameof(MembershipRole.Owner), StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
