using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Shared responses for the management endpoints, in the same problem shapes the rest of the API uses: field
/// guards surface as a <c>400</c> validation problem; a missing/duplicate tenant or key as an RFC 7807 problem with a
/// stable <c>title</c> code.</summary>
internal static class ManagementProblems
{
    /// <summary>The create-tenant body failed a field guard.</summary>
    public static IResult InvalidTenant(IDictionary<string, string[]> errors) => Results.ValidationProblem(errors);

    /// <summary>The tenant id is already taken (create).</summary>
    public static IResult TenantExists(string id) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "tenant_exists", detail: $"a tenant with id '{id}' already exists");

    /// <summary>No tenant with this id (get/suspend/reactivate/issue/list/revoke).</summary>
    public static IResult TenantNotFound(string id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "tenant_not_found", detail: $"no tenant with id '{id}'");

    /// <summary>No such active key for this tenant (revoke) — unknown, foreign, or already revoked.</summary>
    public static IResult KeyNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "key_not_found", detail: "no such active key for this tenant");
}
