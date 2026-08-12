using System.Text.RegularExpressions;

namespace Crawldad.Web.Features.Fixtures;

/// <summary>The fixture-set name rule, shared by the record endpoint's route-name guard and any future validator so they
/// agree by construction. A set name is the Marten document id and the value a replay names via
/// <c>options.fixtureSet</c>: a lowercase slug (alnum + hyphen, 1..64, no leading/trailing hyphen), excluding <c>:</c>
/// so it never collides with the tenant-namespaced key conventions.</summary>
internal static partial class FixtureNameRules
{
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NameSlug();

    /// <summary>Whether <paramref name="name"/> is a valid fixture-set name slug.</summary>
    public static bool IsValidName(string? name) => name is not null && NameSlug().IsMatch(name);
}
