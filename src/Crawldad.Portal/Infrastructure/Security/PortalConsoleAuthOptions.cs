namespace Crawldad.Portal.Infrastructure.Security;

/// <summary>The portal's console-auth knobs, bound from <c>Crawldad:ConsoleAuth</c> (issue #119 PR4) — the portal side of
/// the same section the API reads. When configured, the portal acquires its first-party managed-identity token for
/// <see cref="Audience"/> (the API's App ID URI) and its dashboard READ pages call the API through the console credential
/// (with the stored key as fallback) instead of the stored key alone. Both empty (the default) ⇒ console-mode off and the
/// portal's stored-key behaviour is byte-for-byte unchanged. Config-gated exactly like the email / Data-Protection modules;
/// half-configured fails fast at boot (<see cref="PortalConsoleAuthOptionsValidator"/>). Carries no secret — the token is
/// acquired from the platform managed identity, never a static credential.</summary>
public sealed class PortalConsoleAuthOptions
{
    /// <summary>The configuration section these bind from — the portal side of the API's <c>Crawldad:ConsoleAuth</c>.</summary>
    public const string Section = "Crawldad:ConsoleAuth";

    /// <summary>The Entra directory (tenant) GUID the portal's managed identity lives in. Empty ⇒ console-mode disabled.
    /// Documents the directory; the token is acquired against <see cref="Audience"/>.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>The API App Registration's App ID URI (e.g. <c>api://crawldad-api-stg</c>), used as the token audience —
    /// the portal requests a token for <c>{Audience}/.default</c>. Empty ⇒ console-mode disabled. Both set is the enabled
    /// posture; neither is disabled; exactly one is a misconfiguration the validator rejects. The module reads the section
    /// directly at registration time (IOptions is not yet available) to decide whether to wire console-mode; whether the
    /// portal is actually in console-mode at runtime is decided by the presence of the console client factory (surfaced as
    /// <c>IPortalTenantContext.ConsoleConfigured</c>), not by re-deriving it here.</summary>
    public string Audience { get; init; } = "";
}
