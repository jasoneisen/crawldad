using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Features.Billing;

/// <summary>Shared billing responses, in the RFC 7807 problem shapes the rest of the API uses. All are deliberately
/// benign: an unconfigured provider is a friendly <c>503</c> (never a 500), and every other case a stable-<c>title</c>
/// <c>400</c>.</summary>
internal static class BillingProblems
{
    /// <summary>The provider is not configured/wired — the session endpoints' fail-closed, never-500 state.</summary>
    public static IResult NotConfigured() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "billing_not_configured",
            detail: "Billing is not yet available for this deployment.");

    /// <summary>The requested checkout target is unknown or not a self-serve (purchasable) tier. A field-validation
    /// problem (like the management endpoints' guards) so a typed client surfaces it as a validation error.</summary>
    public static IResult UnknownTier(string? tier) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["tier"] = [$"tier '{tier}' is not a purchasable plan"],
        });

    /// <summary>The inbound webhook could not be verified or parsed — rejected before anything is acted on.</summary>
    public static IResult InvalidWebhook() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_webhook",
            detail: "the billing webhook signature was invalid or the event could not be parsed");
}
