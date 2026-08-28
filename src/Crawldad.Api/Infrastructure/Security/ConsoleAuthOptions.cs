namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The console-principal auth knobs, bound from <c>Crawldad:ConsoleAuth</c> (issue #119 PR2). They point the
/// config-gated <see cref="ConsoleAuthModule"/> scheme at the Entra directory that issues the portal UAMI's first-party
/// access token (<see cref="TenantId"/>) and the API's App-ID-URI audience (<see cref="Audience"/>) the AppRole is
/// exposed on. Both set ⇒ the <c>ConsolePrincipal</c> JwtBearer scheme is registered (still inert until an endpoint opts
/// in — PR5); both empty (the default) ⇒ the scheme is never added and <c>ApiKey</c> stays the sole/default scheme, so an
/// unconfigured host is byte-for-byte unchanged. Half-configured fails fast at boot (<see cref="ConsoleAuthOptionsValidator"/>).
/// Deliberately carries NO signing-key or issuer-override knob: signing keys come only from Entra's published metadata,
/// so no production config value can inject a key or a trusted issuer (the review's finding #8).</summary>
public sealed class ConsoleAuthOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Crawldad:ConsoleAuth";

    /// <summary>The default AppRole value the portal UAMI must carry — the Azure-native "which client may call this API"
    /// control (decision addendum #4). Central grant/revoke on the App Registration, not a mis-settable appid pin.</summary>
    public const string DefaultRequiredRole = "Console.Access";

    /// <summary>The Entra directory (tenant) GUID that issues the portal's v1.0 access tokens. Empty ⇒ the scheme is
    /// disabled. Its v1.0 issuer is <c>https://sts.windows.net/&lt;TenantId&gt;/</c> and its JWKS is published at the
    /// tenant's OIDC metadata endpoint.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>The API App Registration's App ID URI, used as the token audience (e.g. <c>api://crawldad-api-stg</c>).
    /// Empty ⇒ the scheme is disabled.</summary>
    public string Audience { get; init; } = "";

    /// <summary>The AppRole value the validated token's <c>roles</c> claim must contain (fail-closed). Defaults to
    /// <see cref="DefaultRequiredRole"/>; only the portal UAMI's service principal is assigned it.</summary>
    public string RequiredRole { get; init; } = DefaultRequiredRole;

    /// <summary>Whether the console-principal scheme is enabled — both <see cref="TenantId"/> and <see cref="Audience"/>
    /// configured. Neither set is the default disabled posture; exactly one set is a misconfiguration the validator rejects.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(Audience);
}
