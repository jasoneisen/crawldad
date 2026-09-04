using Crawldad.Portal.Auth;
using Crawldad.Portal.Billing;
using Crawldad.Portal.Components;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Runs;
using Crawldad.Portal.Tenancy;
using Marten;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Crawldad.Portal;

/// <summary>All host wiring for the portal lives here (not in Program.cs) so that booting through
/// WebApplicationFactory in the tests exercises — and therefore covers — every line of configuration. The portal
/// is a self-contained Blazor SSR host: it does not reference the API, and its Marten store is isolated in the
/// "portal" schema.</summary>
public static class PortalHost
{
    public static WebApplicationBuilder AddCrawldadPortal(this WebApplicationBuilder builder)
    {
        AddPortalMartenStore(builder);

        // Time is a DI seam — the integration tests swap in a controllable clock to drive code expiry.
        builder.Services.AddSingleton(TimeProvider.System);

        // Passwordless email-OTP services.
        builder.Services.AddSingleton<IOtpCodeGenerator, OtpCodeGenerator>();
        builder.Services.AddScoped<IPortalAuthService, PortalAuthService>();
        EmailModule.AddEmailSending(builder.Services, builder.Configuration, builder.Environment);

        // The durable Data-Protection key ring underlies the portal's auth cookie + antiforgery tokens (the tenant-key
        // protector it used to also back is gone — issue #119 retired the stored-key path). Registered once, up front.
        // Configured (Azure) => persisted + Key-Vault-wrapped so cookies survive redeploys; unconfigured (dev/tests) => the
        // framework's default local ring. Mirrors the API's DataProtectionModule.
        DataProtectionModule.AddKeyRingProtection(builder.Services, builder.Configuration);

        AddWorkspaceResolution(builder);

        AddCookieAuthentication(builder);

        // Blazor Web App — Interactive Server render mode ONLY (no WebAssembly). Almost every page is static SSR (no
        // @rendermode), which is what lets the login form handler call HttpContext.SignInAsync to issue the cookie.
        // The ONE exception is the live-trace page (/app/live/{runId}, @rendermode InteractiveServer), which runs over
        // a circuit with a null cascaded HttpContext — so cookie ISSUANCE must stay on this static-SSR POST and never
        // move into an interactive page. The live page only READS auth state (via AuthenticationStateProvider, below),
        // it never issues a cookie.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Auth state for the interactive circuit. The static-SSR pages read HttpContext.User directly (cascaded), but a
        // circuit has no HttpContext — so the live page reads the signed-in user through AuthenticationStateProvider,
        // which the framework seeds from the authenticated connection on a circuit (and from HttpContext.User during
        // prerender). This flows only the identity claims (already in the auth cookie, browser-visible); the tenant API
        // key is never part of it. AddCascadingAuthenticationState also makes <AuthorizeView>/cascading auth available.
        builder.Services.AddCascadingAuthenticationState();

        return builder;
    }

    public static WebApplication MapCrawldadPortal(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            // Production: ProblemPage → the Blazor error page, and enforce HSTS. Development gets the developer
            // exception page instead.
            app.UseExceptionHandler("/error", createScopeForErrors: true);
            app.UseHsts();
        }

        // Render the NotFound page for server-side unmatched routes (endpoint routing 404s with an empty body
        // before the Blazor router ever sees them). The original 404 status is preserved.
        app.UseStatusCodePagesWithReExecute("/not-found");

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets();

        app.MapPortalAuth();
        // The cookie-gated screenshot proxy the run-detail page's <img> tags point at (the browser holds no API key).
        app.MapPortalRunScreenshots();
        app.MapBillingUi(); // the billing card's checkout / portal form handlers (POST → SDK → redirect)
        app.MapWorkspaceSwitch(); // the shell switcher's form handler (POST → persist active workspace → redirect)
        app.MapWorkspaceProvision(); // the account "create your free workspace" affordance (POST → provision → link+select → redirect)
        app.MapPortalMembers(); // the account Members card's add / change-role / remove form handlers (Owner-only, PRG)
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }

    // The portal's OWN Marten store on the shared Postgres, plus its boot-time schema provisioning. Connection-string
    // convention mirrors the API ("marten"); DatabaseSchemaName "portal" keeps it fully isolated from the API's
    // "crawldad" schema.
    private static void AddPortalMartenStore(WebApplicationBuilder builder)
    {
        var connection = builder.Configuration.GetConnectionString("marten")!;
        var marten = builder.Services.AddMarten(options => ConfigurePortalMarten(options, connection));

        // Provision the "portal" schema at boot in EVERY environment — unconditionally, unlike the API, which gates this
        // on Development because it has an out-of-band `dotnet run -- db-apply`. The portal has no such verb: Program.cs
        // only ever serves (Dockerfile.portal's entrypoint takes no CLI arguments), so without this line Staging and
        // Production would fall back to Marten's default AutoCreate.CreateOrUpdate materialising the three portal tables
        // lazily on FIRST DOCUMENT USE. That first-touch DDL races: production runs 1-3 replicas, so two replicas serving
        // their first OTP request concurrently can collide mid-create (42P01 "relation does not exist" / a duplicate-object
        // error) and a real customer's sign-in fails.
        //
        // The startup apply is NOT a race of its own — verified by decompiling Marten 9.31.2 + Weasel 9.30.0.
        // ApplyAllDatabaseChangesOnStartup() registers the MartenActivator hosted service, which implements
        // IGlobalLock<NpgsqlConnection> and passes ITSELF to the LOCKED
        // DatabaseBase.ApplyAllConfiguredChangesToDatabaseAsync(IGlobalLock<T>, ...) overload — so concurrent appliers
        // serialise on a Postgres advisory lock keyed by StoreOptions.ApplyChangesLockId (four attempts, 0/50/100/250 ms).
        // The winner applies; a replica that still cannot attain it fails its own boot (Weasel throws under the default
        // ResourceMigrationFailureMode.FailFast) and Container Apps restarts it into a schema the winner has by then
        // applied. A boot that fails and is restarted into a provisioned schema is a far better failure mode than a
        // customer's login 500ing on a half-created table. Steady-state boots are cheap: Weasel short-circuits on a
        // matching schema fingerprint before it ever reaches for the lock.
        marten.ApplyAllDatabaseChangesOnStartup();
    }

    // The portal's Marten document model, in the isolated "portal" schema. Each identity is the normalized email, so a user
    // and their active-workspace selection line up 1:1 per account. There is NO portal-side tenant link anymore (issue #119
    // simplification): the API's membership store is the sole authority for which workspaces a user may act as; the portal
    // keeps only the active-workspace pointer below. Any legacy PortalTenantLink rows from the retired stored-key path are
    // simply ignored — the document type is no longer mapped, so nothing ever loads them (cleanest, no migration).
    private static void ConfigurePortalMarten(StoreOptions options, string connectionString)
    {
        options.Connection(connectionString);
        options.DatabaseSchemaName = "portal";

        // Email is the account identity → unique by construction, and case-insensitive because we always store it
        // lower-invariant.
        options.Schema.For<PortalUser>().Identity(u => u.Email);
        options.Schema.For<PortalWorkspaceSelection>().Identity(s => s.Email);

        // Optimistic concurrency on the challenge: parallel verify attempts serialize on the document version, so the
        // per-challenge attempt cap can't be beaten by racing guesses (see PortalAuthService.VerifyCodeAsync).
        options.Schema.For<OtpChallenge>().UseOptimisticConcurrency(true);
    }

    // Cookie authentication — the ASP.NET cookie handler directly, no ASP.NET Identity framework.
    private static void AddCookieAuthentication(WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "crawldad.portal.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Dev runs over http; production terminates TLS in front, so only send the cookie over https there.
                options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.LoginPath = "/login";
                options.LogoutPath = "/auth/signout";
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
            });
        builder.Services.AddAuthorization();
    }

    // Portal → Crawldad workspace resolution + the per-request typed API client (Crawldad.Client). The portal is
    // console-mode only (issue #119): the signed-in user's ACTIVE workspace (normalized email → tenant id, a preference
    // pointer) is resolved per request by IPortalTenantContext, which hands the data pages a CrawldadClient authenticated as
    // the portal's first-party CONSOLE identity for that workspace — or a clean null/NotLinkedException when the request is
    // unauthenticated, has no active workspace, or console access is unconfigured. There is NO stored tenant key.
    private static void AddWorkspaceResolution(WebApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        // The active-workspace pointer (email → tenant id): the console-resolution bootstrap and the switcher preference.
        // It is only a preference — the API's membership store decides which workspaces a user may actually act as.
        builder.Services.AddSingleton<IPortalWorkspaceSelectionStore, MartenPortalWorkspaceSelectionStore>();
        builder.Services.AddScoped<IPortalTenantContext, PortalTenantContext>();

        // The circuit-safe resolver the interactive live-trace page uses: same active workspace → same console client as the
        // per-request context, but sourced from AuthenticationStateProvider instead of the (circuit-null) HttpContext.
        builder.Services.AddScoped<ICircuitTenantResolver, CircuitTenantResolver>();

        // The account area's "claim an existing workspace" path: validates a submitted key against the live API, records the
        // account's Owner membership, and ALWAYS discards the key (never stored). Stateless over the pooled API HttpClient.
        builder.Services.AddScoped<IWorkspaceLinker, WorkspaceLinker>();

        // Self-serve free-workspace provisioning (issue #119): calls the API's console-only provisioning endpoint and, on
        // success, selects the new workspace. Its ConsoleClientFactory dependency is present only in console-mode (optional
        // ctor param → null when console access is unconfigured), which is exactly when the service reports "unavailable".
        builder.Services.AddScoped<IPortalProvisioningService, PortalProvisioningService>();

        // The public signup flow's post-verification landing decision: a zero-workspace account is provisioned its free
        // workspace and lands on the first-run dashboard; a returning account lands like a /login. Scoped because it depends
        // on the scoped provisioning service; the signup page is the only caller.
        builder.Services.AddScoped<ISignupLanding, SignupLanding>();

        // One pooled HttpClient the SDK rides on, base address from config (validated at boot so a missing/malformed
        // URL fails loudly here, not on the first API call). The per-request console credential is applied by the client.
        var apiBaseUrl = PortalTenancy.ResolveApiBaseUrl(builder.Configuration);
        builder.Services.AddHttpClient(PortalTenancy.ApiHttpClientName, client => client.BaseAddress = apiBaseUrl);

        // Console-mode (issue #119): when Crawldad:ConsoleAuth is configured, register the managed-identity token source +
        // console client factory so dashboard reads/writes ride the portal's first-party console credential. Unconfigured
        // (an operator misconfig) => nothing registered => IPortalTenantContext resolves its honest "console access not
        // configured" state. Dev/CI configure the section with a test/fake token source (the same DI-replacement pattern the
        // tests use). Registered after the pooled HttpClient it wraps.
        PortalConsoleAuthModule.AddConsoleAuth(builder.Services, builder.Configuration);
    }

}
