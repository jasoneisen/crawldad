using System.Text.RegularExpressions;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>The webhook-name slug rule, shared by the register endpoint's route-name guard and any other consumer so they
/// agree by construction. A name is the Marten document id, unique per tenant — the same lowercase slug shape the browsers
/// slice uses.</summary>
internal static partial class WebhookRegistrationRules
{
    // A lowercase slug (alnum + hyphen, 1..64, no leading/trailing hyphen). The webhook name is the document id.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NameSlug();

    /// <summary>Whether <paramref name="name"/> is a valid webhook name slug.</summary>
    public static bool IsValidName(string? name) => name is not null && NameSlug().IsMatch(name);
}
