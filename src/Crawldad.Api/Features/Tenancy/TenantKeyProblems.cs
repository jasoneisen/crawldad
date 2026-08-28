using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Shared problem responses for the tenant self-service key endpoints (<c>/tenant/keys</c>), in the same RFC 7807
/// shapes the rest of the API uses, each with a stable <c>title</c> code. Key material never appears in any of these —
/// only codes and guidance.</summary>
internal static class TenantKeyProblems
{
    /// <summary>The caller is an env-configured tenant, not a registry tenant: its keys are operator config, and a
    /// registry-minted self-service key would never authenticate (no backing <c>RegistryTenant</c> — the dead-key trap),
    /// so the whole surface is refused. A clear <c>400</c> with a stable code.</summary>
    public static IResult SelfServiceUnavailable() =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "self_service_unavailable",
            detail: "self-service API key management is available to registry tenants only; this tenant's keys are managed by the Crawldad operator");

    /// <summary>Revoking this key would leave the tenant with no active key — self-lockout, since self-service auth needs a
    /// live key. Refused; rotate it instead (mint-then-revoke leaves no gap). <c>409</c>.</summary>
    public static IResult LastActiveKey() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "last_active_key",
            detail: "cannot revoke the tenant's last active key; rotate it (POST /tenant/keys/{id}/rotate) to replace it without a gap, or mint another key first");

    /// <summary>The key being revoked is the one authenticating this very request; revoking it would break the caller
    /// mid-session. Refused; rotate it instead (a rotate is allowed to replace the current key). <c>409</c>.</summary>
    public static IResult CurrentKey() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "current_key",
            detail: "cannot revoke the key authenticating this request; rotate it instead (POST /tenant/keys/{id}/rotate)");

    /// <summary>No such active key for this tenant (rotate/revoke) — unknown, another tenant's (no existence oracle), or
    /// already revoked. <c>404</c>.</summary>
    public static IResult KeyNotFound() =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "key_not_found",
            detail: "no such active key for this tenant");

    /// <summary>The optional key label failed validation (too long). A <c>400</c> in the RFC 7807 validation-problem shape,
    /// so it surfaces through the SDK's <c>CrawldadValidationException</c> like any other field guard.</summary>
    public static IResult InvalidLabel(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["label"] = [message] });
}
