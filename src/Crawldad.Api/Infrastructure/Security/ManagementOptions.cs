namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The interim management-API credential, bound from <c>Management</c>. A single bearer key authorizes the
/// server-side management endpoints (tenant + key administration) the future portal consumes. When
/// <see cref="ApiKey"/> is unset the management surface is disabled entirely — its routes are never mapped, so every
/// <c>/management/…</c> request is a plain 404. This is deliberately a stop-gap until the portal grows real operator
/// auth; see THREAT_MODEL.md.</summary>
public sealed class ManagementOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string Section = "Management";

    /// <summary>The management bearer key. Presented as <c>Authorization: Bearer &lt;key&gt;</c> and compared in constant
    /// time. Blank/absent disables the management endpoints.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Whether the management surface is enabled (a key is configured).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}
