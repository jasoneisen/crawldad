namespace Crawldad.Api.Features.Tenancy;

/// <summary>Field rules for tenant self-service API keys. Today just the optional key label — trimmed and length-bounded
/// metadata to tell a tenant's keys apart in a listing; never load-bearing for authentication.</summary>
internal static class TenantKeyRules
{
    /// <summary>The longest accepted key label.</summary>
    public const int MaxLabelLength = 64;

    /// <summary>Normalizes an optional key label. An absent/blank label is <c>(null, null)</c> — an unlabelled key. A
    /// present label is trimmed; if it still exceeds <see cref="MaxLabelLength"/> it is rejected with a message. Otherwise
    /// the trimmed label is returned.</summary>
    /// <param name="label">The caller-supplied label, or null.</param>
    /// <returns>The normalized label (null when unlabelled) and a validation error message (null when valid).</returns>
    public static (string? Label, string? Error) NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return (null, null);
        }

        var trimmed = label.Trim();
        return trimmed.Length > MaxLabelLength
            ? (null, $"label must be at most {MaxLabelLength} characters")
            : (trimmed, null);
    }
}
