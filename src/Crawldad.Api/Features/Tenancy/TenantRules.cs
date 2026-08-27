using System.Text.RegularExpressions;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>The registry-tenant field rules, shared by the create endpoint's guard. A tenant id is the Marten conjoined
/// partition key and the per-tenant secret-vault namespace, so it is constrained to the same lowercase slug the browser
/// name uses (which excludes <c>':'</c>, keeping the <c>Secrets:{tenant}:{ref}</c> namespace unambiguous — the same
/// invariant <see cref="Crawldad.Api.Infrastructure.Security.TenantRegistry"/> enforces on a configured tenant id).</summary>
internal static partial class TenantRules
{
    /// <summary>The longest accepted display name.</summary>
    public const int MaxDisplayNameLength = 200;

    /// <summary>The longest accepted tier moniker.</summary>
    public const int MaxTierLength = 64;

    // Lowercase slug: alnum + hyphen, 1..64, no leading/trailing hyphen, and no ':'.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdSlug();

    /// <summary>Whether <paramref name="id"/> is a valid tenant id slug.</summary>
    public static bool IsValidId(string? id) => id is not null && IdSlug().IsMatch(id);

    /// <summary>Whether <paramref name="displayName"/> is present and within the length bound.</summary>
    public static bool IsValidDisplayName(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName) && displayName.Length <= MaxDisplayNameLength;

    /// <summary>Whether <paramref name="tier"/> is within the length bound (empty/absent is allowed — it defaults).</summary>
    public static bool IsValidTier(string? tier) => tier is null || tier.Length <= MaxTierLength;

    /// <summary>Whether <paramref name="slotAllowance"/> is unset or a positive cap (a 0/negative override is a misconfiguration).</summary>
    public static bool IsValidSlotAllowance(int? slotAllowance) => slotAllowance is null or >= 1;
}
