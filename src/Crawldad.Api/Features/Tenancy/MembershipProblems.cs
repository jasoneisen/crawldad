using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Shared problem responses for the tenant self-service membership endpoints (<c>/tenant/memberships</c>), in the
/// same RFC 7807 shapes the rest of the API uses, each with a stable <c>title</c> code.</summary>
internal static class MembershipProblems
{
    /// <summary>The caller is an env-configured tenant, not a registry tenant: memberships (the console authority) only
    /// ever reference registry tenants, so the surface is refused for an env tenant. A clear <c>400</c> with a stable
    /// code (parity with <see cref="TenantKeyProblems.SelfServiceUnavailable"/>).</summary>
    public static IResult SelfServiceUnavailable() =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "self_service_unavailable",
            detail: "membership management is available to registry tenants only; this tenant is operator-managed");

    /// <summary>The request omitted the member email. A <c>400</c> in the RFC 7807 validation-problem shape, so it surfaces
    /// through the SDK's <c>CrawldadValidationException</c> like any other field guard.</summary>
    public static IResult InvalidEmail(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["email"] = [message] });
}
