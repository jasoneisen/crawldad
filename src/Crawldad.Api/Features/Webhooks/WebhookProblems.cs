using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>Shared <c>400</c> responses for the webhooks endpoints. The name is a route key (not part of the validated
/// body), so its slug guard surfaces here in the same validation-problem shape the body validator produces.</summary>
internal static class WebhookProblems
{
    /// <summary>The registered name is not a valid slug (lowercase alnum + hyphen, 1..64, no leading/trailing hyphen).</summary>
    public static IResult InvalidName() => Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["name"] = ["name must be a lowercase slug of letters, digits, and hyphens (1-64 chars, no leading/trailing hyphen)"],
    });
}
