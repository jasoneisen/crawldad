using System.Security.Claims;
using Crawldad.Contracts;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The config-gated <c>ConsolePrincipal</c> authentication scheme (issue #119 PR2). A second, NON-default
/// <see cref="JwtBearerDefaults">JwtBearer</see> scheme that validates the portal's first-party Entra access token, so
/// the console can one day call the API as itself rather than on a stored tenant key. It is registered <b>only when
/// <c>Crawldad:ConsoleAuth</c> is configured</b> — the <see cref="DataProtectionModule"/>/<see cref="Crawldad.Api.Features.Tenancy.ManagementModule"/>
/// precedent (absent config ⇒ the scheme literally isn't added). It is added as a non-default scheme, and only the
/// explicitly-enumerated console-read endpoints opt into it via the <c>ConsoleOrKey</c> policy (PR4), so <c>ApiKey</c>
/// remains the sole/default scheme everywhere else.
///
/// <para><b>Membership is the authority (PR4).</b> After the token validates as the portal, the handler reads two SDK
/// selector headers — <see cref="ConsoleAuthHeaders.ConsoleUser"/> and <see cref="ConsoleAuthHeaders.Workspace"/> — and
/// resolves them against the API's own <see cref="ITenantMembershipStore"/>. Only an <b>active membership for an active
/// tenant</b> stamps the <c>crawldad:tenant_id</c>/<c>crawldad:actor</c> claims the pipeline reads; absent that it stamps
/// nothing, so the <c>ConsoleOrKey</c> policy denies the request as a <c>403</c>. The header is a <b>selector over
/// already-granted authority</b>, never a capability — a forged header can at worst name a user, and the request still
/// resolves to whatever <c>(email, tenant)</c> membership actually exists. Strict precedence closes the two-scheme merge
/// (finding #4): a request presenting <b>both</b> the console token and an <c>X-Api-Key</c> is rejected, never merged. The
/// stamped actor is the human email (decision addendum #1), so attribution follows the channel.</para>
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
    /// where an authorization policy explicitly names it — i.e. exactly the endpoints carrying <see cref="ConsoleOrKeyPolicy"/>.</summary>
    public const string Scheme = "ConsolePrincipal";

    /// <summary>The named authorization policy the enumerated console-read endpoints opt into (PR4). It accepts the
    /// <c>ApiKey</c> scheme <b>and</b> — when the console scheme is configured — <see cref="Scheme"/>, then requires the
    /// principal to carry a tenant claim (so a portal token with no membership is a <c>403</c>, not silent access). Every
    /// other endpoint keeps the default <c>ApiKey</c>-only gate. When <c>Crawldad:ConsoleAuth</c> is unconfigured the policy
    /// is <c>ApiKey</c>-only, so a console-read endpoint behaves byte-for-byte as it does today.</summary>
    public const string ConsoleOrKeyPolicy = "ConsoleOrKey";

    /// <summary>The named authorization policy the <b>Owner-only</b> console endpoints opt into (issue #119 PR6):
    /// key management (mint/rotate/revoke) and membership management (add/remove/change-role). Same base as
    /// <see cref="ConsoleOrKeyPolicy"/> (accepts <c>ApiKey</c> and — when configured — <see cref="Scheme"/>, and requires a
    /// tenant claim) plus a <see cref="ConsoleOwnerRequirement"/>: a request authenticated by the <b>console</b> scheme must
    /// carry an explicit <c>Owner</c> role, while a request authenticated by an <b>API key</b> is unrestricted (key
    /// possession is full tenant authority). So a console Member is a <c>403</c> here but reaches every
    /// <see cref="ConsoleOrKeyPolicy"/> endpoint, and a programmatic key caller is unaffected.</summary>
    public const string ConsoleOwnerOrKeyPolicy = "ConsoleOwnerOrKey";

    /// <summary>The raw v1.0 token version claim (read directly because <see cref="JwtBearerOptions.MapInboundClaims"/> is false).</summary>
    internal const string VersionClaim = "ver";

    /// <summary>The required v1.0 token version — a v2-shaped token is rejected (this scheme pins the managed-identity v1.0 shape).</summary>
    internal const string TokenVersion = "1.0";

    /// <summary>The raw AppRole claim a v1.0 access token carries (read directly under <see cref="JwtBearerOptions.MapInboundClaims"/> = false).</summary>
    internal const string RolesClaim = "roles";

    /// <summary>Registers the console-auth options + boot guard, the always-present <see cref="ConsoleOrKeyPolicy"/>
    /// authorization policy, and — only when the section is fully configured — the non-default <c>ConsolePrincipal</c>
    /// JwtBearer scheme. Absent config the scheme is never added and the policy is <c>ApiKey</c>-only, so the host is
    /// byte-for-byte unchanged and <c>ApiKey</c> stays the sole/default scheme.</summary>
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

        // The ConsoleOrKey / ConsoleOwnerOrKey policies always exist (so the enumerated endpoints' [Authorize(Policy=…)]
        // never reference an unregistered policy), but only list the ConsolePrincipal scheme when it is configured —
        // unconfigured, they are ApiKey-only, identical to the default gate. The Owner handler is registered always: it lets
        // a key-authenticated Owner-endpoint request through unchanged (no role gate on the key channel) and gates the
        // console channel on the Owner role.
        AddConsoleOrKeyPolicy(services, options.Enabled);
        AddConsoleOwnerOrKeyPolicy(services, options.Enabled);
        services.AddSingleton<IAuthorizationHandler, ConsoleOwnerAuthorizationHandler>();

        if (!options.Enabled)
        {
            return; // no config → the scheme is never added → inert; ApiKey remains the sole/default scheme
        }

        // AddAuthentication() with no argument does NOT change the default scheme (set to ApiKey elsewhere); it just
        // returns the builder so ConsolePrincipal is added as a non-default scheme (finding #4 — it runs only where a
        // policy names it, i.e. the ConsoleOrKey endpoints).
        services.AddAuthentication().AddJwtBearer(Scheme, jwt => ConfigureJwtBearer(jwt, options));
    }

    // Registers the ConsoleOrKey authorization policy. It authenticates ApiKey (always) and — when the console scheme is
    // configured — ConsolePrincipal, requires an authenticated user, and requires a tenant claim so a portal token without
    // a membership is a 403 rather than silent access. The default policy stays ApiKey-only (RequireAuthorizeOnAll), so
    // ConsolePrincipal is reachable ONLY on an endpoint that names this policy.
    private static void AddConsoleOrKeyPolicy(IServiceCollection services, bool consoleEnabled)
    {
        var schemes = consoleEnabled
            ? new[] { CrawldadAuthentication.Scheme, Scheme }
            : new[] { CrawldadAuthentication.Scheme };

        services.Configure<AuthorizationOptions>(options =>
            options.AddPolicy(ConsoleOrKeyPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(schemes);
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(CrawldadClaims.TenantId); // no tenant claim (no membership) ⇒ 403, not access
            }));
    }

    // Registers the ConsoleOwnerOrKey policy (issue #119 PR6): the ConsoleOrKey base (same schemes + authenticated + tenant
    // claim) plus the ConsoleOwnerRequirement, which admits a key-authenticated principal unconditionally and a console
    // principal only when it carries the Owner role. Layered as a separate policy so the enumerated Owner-only endpoints opt
    // into it explicitly; every other console endpoint keeps ConsoleOrKey (Member-reachable).
    private static void AddConsoleOwnerOrKeyPolicy(IServiceCollection services, bool consoleEnabled)
    {
        var schemes = consoleEnabled
            ? new[] { CrawldadAuthentication.Scheme, Scheme }
            : new[] { CrawldadAuthentication.Scheme };

        services.Configure<AuthorizationOptions>(options =>
            options.AddPolicy(ConsoleOwnerOrKeyPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(schemes);
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(CrawldadClaims.TenantId); // no tenant claim (no membership) ⇒ 403 before the role gate
                policy.AddRequirements(new ConsoleOwnerRequirement());
            }));
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

        // Fail-closed AppRole + version check, then the membership resolution that maps the portal token to a tenant. A
        // token that validates but is not v1.0, or lacks the required AppRole, or presents an API key alongside the console
        // token, is rejected (context.Fail → 401). A valid portal token with an active membership stamps the tenant claims;
        // one without stamps nothing, so the ConsoleOrKey policy denies it as a 403.
        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = context => OnTokenValidatedAsync(context, requiredRole),
        };
    }

    // The post-validation gate: prove the portal (version + role), reject the both-credentials merge, then resolve the
    // selector headers to a membership and stamp the tenant/actor claims. Extracted so every branch is unit-testable
    // through the CI test-issuer harness (a fake membership/registry store in the request container).
    private static async Task OnTokenValidatedAsync(TokenValidatedContext context, string requiredRole)
    {
        var principal = context.Principal!; // non-null once the token has validated
        if (!string.Equals(principal.FindFirstValue(VersionClaim), TokenVersion, StringComparison.Ordinal))
        {
            context.Fail("Console token is not a v1.0 access token.");
            return;
        }

        if (!principal.HasClaim(RolesClaim, requiredRole))
        {
            context.Fail("Console token is missing the required AppRole.");
            return;
        }

        var request = context.HttpContext.Request;

        // Strict precedence (finding #4): a request carrying BOTH the portal token and an API key is ambiguous about which
        // identity it intends — reject it rather than let the two schemes merge into one principal. On an ApiKey-only
        // request this handler never runs (no bearer token to validate), so the selectors are ignored there by construction.
        if (!string.IsNullOrWhiteSpace(request.Headers[CrawldadAuthentication.ApiKeyHeader].ToString()))
        {
            context.Fail("A request may present the console token or an API key, not both.");
            return;
        }

        await StampMembershipClaimsAsync(context);
    }

    // Resolves the (workspace, user) selectors to an active membership for an active tenant and, only then, stamps the
    // tenant/actor claims. Any miss (no selectors, no membership, unknown/suspended tenant) stamps nothing, leaving the
    // request with no tenant claim so the ConsoleOrKey policy denies it as a 403 — authority is the membership store.
    private static async Task StampMembershipClaimsAsync(TokenValidatedContext context)
    {
        var request = context.HttpContext.Request;

        // The selectors: the active workspace (tenant GUID) and the verified user (re-normalized here — the header's casing
        // is never trusted).
        var workspace = request.Headers[ConsoleAuthHeaders.Workspace].ToString();
        var rawUser = request.Headers[ConsoleAuthHeaders.ConsoleUser].ToString();
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(rawUser))
        {
            return;
        }

        var email = EmailAddress.Normalize(rawUser);
        var services = context.HttpContext.RequestServices;
        var ct = context.HttpContext.RequestAborted;

        // Authority: an ACTIVE membership for this (workspace, email). None → not a member → no claim → 403.
        var membership = await services.GetRequiredService<ITenantMembershipStore>().FindActiveAsync(workspace, email, ct);
        if (membership is null)
        {
            return;
        }

        // The workspace must be a live registry tenant — a suspended (or unknown) tenant is rejected exactly as the key
        // path rejects a suspended tenant's key. Memberships only ever reference registry tenants, so a null here is a
        // stale/mismatched selector, not an env tenant.
        var tenant = await services.GetRequiredService<ITenantRegistryStore>().FindAsync(membership.TenantId, ct);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            return;
        }

        // Stamp the SAME tenant/actor claims the ApiKey scheme issues, so Wolverine tenant detection (IsClaimTypeNamed) and
        // TenantContext are unchanged. Exactly one crawldad:tenant_id claim exists (the validated JWT identity carries none),
        // so nothing downstream has to disambiguate. The actor is the human email — console attribution (decision addendum #1).
        // The role claim rides ALONGSIDE them on this console identity (issue #119 PR6): the Owner-only policy reads it to
        // gate key/membership management, so a Member console principal can never act as an Owner. It is stamped ONLY here —
        // a key principal never carries it, which is exactly how the policy tells the two channels apart.
        context.Principal!.AddIdentity(new ClaimsIdentity(
            [
                new Claim(CrawldadClaims.TenantId, membership.TenantId),
                new Claim(CrawldadClaims.Actor, membership.Email),
                new Claim(CrawldadClaims.Role, membership.Role.ToString()),
            ],
            Scheme,
            nameType: CrawldadClaims.Actor,
            roleType: null));
    }
}
