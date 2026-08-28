using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Shared responses for the self-serve provisioning endpoint (issue #119 PR7), in the same problem shapes the rest
/// of the API uses: an already-provisioned email is a <c>409</c> with a stable <c>title</c> code (and the existing workspace
/// as an extension so the portal can recover the link); a missing actor selector or an over-long display name is a
/// <c>400</c>; exceeding the abuse-insurance rate limit is a <c>429</c>.</summary>
internal static class ProvisioningProblems
{
    /// <summary>The stable title of the one-free-tenant-per-email refusal.</summary>
    public const string AlreadyProvisionedTitle = "free_tenant_exists";

    /// <summary>The email already has a free workspace (its lifetime marker exists). The existing workspace id rides as a
    /// <c>tenantId</c> problem extension — it is the caller's own workspace (no cross-account leak), so the portal can
    /// re-establish the link to it rather than stranding the account.</summary>
    public static IResult AlreadyProvisioned(string tenantId) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: AlreadyProvisionedTitle,
            detail: "this account already has a free workspace; additional workspaces are created on a paid plan",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenantId"] = tenantId });

    /// <summary>The request carried no <c>X-Crawldad-Console-User</c> selector, so there is no verified actor to provision
    /// for. The console scheme authenticated the portal, but provisioning is attributed to the human email it names.</summary>
    public static IResult ActorRequired() =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "actor_required",
            detail: "the console-user selector header is required to provision a free workspace");

    /// <summary>The optional display name exceeded the length bound.</summary>
    public static IResult InvalidDisplayName(int maxLength) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["displayName"] = [$"displayName must be at most {maxLength} characters"],
        });

    /// <summary>Too many provision attempts for this account in a short window (abuse insurance).</summary>
    public static IResult RateLimited() =>
        Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "provisioning_rate_limited",
            detail: "too many free-workspace provision attempts for this account in a short window; slow down and retry shortly");
}
