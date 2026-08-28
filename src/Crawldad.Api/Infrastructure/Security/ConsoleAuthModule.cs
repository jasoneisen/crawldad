using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The config-gated <c>ConsolePrincipal</c> authentication scheme (issue #119 PR2). A second, NON-default
/// <see cref="JwtBearerDefaults">JwtBearer</see> scheme that validates the portal's first-party Entra access token, so
/// the console can one day call the API as itself rather than on a stored tenant key. It is registered <b>only when
/// <c>Crawldad:ConsoleAuth</c> is configured</b> — the <see cref="DataProtectionModule"/>/<see cref="Crawldad.Api.Features.Tenancy.ManagementModule"/>
/// precedent (absent config ⇒ the scheme literally isn't added). It changes <b>zero</b> runtime behaviour today: it is
/// added as a non-default scheme, and no endpoint opts into it yet (the <c>ConsoleOrKey</c> policy is PR5), so
/// <c>ApiKey</c> remains the sole/default scheme everywhere. This PR does not stamp any tenant/actor claim — mapping a
/// console token to a tenant is the membership store (PR4); here the scheme only proves the caller is the portal.
///
/// <para><b>What it validates</b> (the review's binding finding #5 + decision addendum #4 — AppRole, not an appid pin):
/// a <b>v1.0</b> access token (issuer <c>https://sts.windows.net/&lt;tenant&gt;/</c>, signature from Entra's published
/// JWKS, audience = the API's App ID URI, unexpired), with <see cref="JwtBearerOptions.MapInboundClaims"/> = false so the
/// raw <c>ver</c>/<c>roles</c> claim types are read, then a <b>fail-closed</b> check that the token is <c>ver=1.0</c> and
/// carries the required AppRole (<see cref="ConsoleAuthOptions.RequiredRole"/>, "Console.Access"). Only the portal UAMI's
/// service principal is assigned that role on the App Registration, so the role claim is the Azure-native "which client
/// may call this API" control.</para>
///
/// <para><b>Signing keys come only from Entra metadata</b> — never a static/config key (finding #8). <see cref="ConsoleAuthOptions"/>
/// exposes no key/issuer-override knob, so no production config value can inject a trusted key or issuer. The CI test
/// harness swaps the signing-key <i>source</i> via a test-only <c>IConfigureNamedOptions&lt;JwtBearerOptions&gt;</c> in the
/// test host — a different issuer <i>configuration</i> of the same scheme, exercising the real validator, never a bypass
/// branch and never reachable from production configuration.</para></summary>
public static class ConsoleAuthModule
{
    /// <summary>The console-principal authentication scheme name. A non-default scheme: it authenticates a request only
    /// where an authorization policy explicitly names it (none does yet — PR5).</summary>
    public const string Scheme = "ConsolePrincipal";

    /// <summary>The raw v1.0 token version claim (read directly because <see cref="JwtBearerOptions.MapInboundClaims"/> is false).</summary>
    internal const string VersionClaim = "ver";

    /// <summary>The required v1.0 token version — a v2-shaped token is rejected (this scheme pins the managed-identity v1.0 shape).</summary>
    internal const string TokenVersion = "1.0";

    /// <summary>The raw AppRole claim a v1.0 access token carries (read directly under <see cref="JwtBearerOptions.MapInboundClaims"/> = false).</summary>
    internal const string RolesClaim = "roles";

    /// <summary>Registers the console-auth options + boot guard, and — only when the section is fully configured — the
    /// non-default <c>ConsolePrincipal</c> JwtBearer scheme. Absent config the scheme is never added, so the host is
    /// unchanged and <c>ApiKey</c> stays the sole/default scheme.</summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">The host configuration (the scheme is read from <c>Crawldad:ConsoleAuth</c>).</param>
    public static void AddConsolePrincipal(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConsoleAuthOptions.Section);

        // The knobs + boot guard (a half-configured scheme fails at startup rather than silently failing to authenticate).
        services.AddOptions<ConsoleAuthOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ConsoleAuthOptions>, ConsoleAuthOptionsValidator>();

        // The wiring choice is a registration-time decision, so read the section directly (IOptions isn't available yet) —
        // the same idiom DataProtectionModule uses to select its provider.
        var options = section.Get<ConsoleAuthOptions>() ?? new ConsoleAuthOptions();
        if (!options.Enabled)
        {
            return; // no config → the scheme is never added → inert; ApiKey remains the sole/default scheme
        }

        // AddAuthentication() with no argument does NOT change the default scheme (set to ApiKey elsewhere); it just
        // returns the builder so ConsolePrincipal is added as a non-default scheme (finding #4 — it runs only where a
        // policy names it, and none does yet).
        services.AddAuthentication().AddJwtBearer(Scheme, jwt => ConfigureJwtBearer(jwt, options));
    }

    // Straight-line configuration of the ConsolePrincipal JwtBearer scheme (no branches — so nothing here needs coverage
    // beyond running once), plus the fail-closed OnTokenValidated check whose branches ARE exercised by test-issued tokens.
    private static void ConfigureJwtBearer(JwtBearerOptions jwt, ConsoleAuthOptions options)
    {
        var tenantId = options.TenantId;
        var requiredRole = options.RequiredRole;

        // The v1.0 managed-identity token shape (finding #5): issuer is sts.windows.net (NOT the v2 login.microsoftonline
        // issuer), and its signing keys are published at the tenant's v1.0 OIDC metadata. Pinning the v1.0 metadata means
        // the effective trusted issuer IS sts.windows.net, so a v2-shaped token is rejected at issuer validation. Signing
        // keys come ONLY from that published JWKS — no static/config key is ever set here (finding #8).
        jwt.MetadataAddress = $"https://login.microsoftonline.com/{tenantId}/.well-known/openid-configuration";
        jwt.Audience = options.Audience;
        jwt.MapInboundClaims = false; // read the raw v1.0 claim types (ver/roles), not the mapped long-URI names
        jwt.TokenValidationParameters.ValidIssuer = $"https://sts.windows.net/{tenantId}/";
        jwt.TokenValidationParameters.ValidateIssuer = true;
        jwt.TokenValidationParameters.ValidateAudience = true;
        jwt.TokenValidationParameters.ValidateLifetime = true;
        jwt.TokenValidationParameters.ValidateIssuerSigningKey = true;

        // Fail-closed AppRole + version check: a token that validates but is not v1.0, or does not carry the required
        // AppRole, is rejected — so the scheme authenticates a request only when the caller is the role-assigned portal
        // identity. context.Fail turns the validated ticket into a 401.
        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var principal = context.Principal!; // non-null once the token has validated
                if (!string.Equals(principal.FindFirstValue(VersionClaim), TokenVersion, StringComparison.Ordinal))
                {
                    context.Fail("Console token is not a v1.0 access token.");
                }
                else if (!principal.HasClaim(RolesClaim, requiredRole))
                {
                    context.Fail("Console token is missing the required AppRole.");
                }

                return Task.CompletedTask;
            },
        };
    }
}
