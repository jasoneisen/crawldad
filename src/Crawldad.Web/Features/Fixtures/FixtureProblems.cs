using Microsoft.AspNetCore.Http;

namespace Crawldad.Web.Features.Fixtures;

/// <summary>Shared <c>400</c> responses for the fixtures endpoints. The set name is a route key (not a validated body),
/// so its slug guard surfaces here in the standard validation-problem shape.</summary>
internal static class FixtureProblems
{
    /// <summary>The fixture-set name is not a valid slug (lowercase alnum + hyphen, 1..64, no leading/trailing hyphen).</summary>
    public static IResult InvalidName() => Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["name"] = ["name must be a lowercase slug of letters, digits, and hyphens (1-64 chars, no leading/trailing hyphen)"],
    });
}
