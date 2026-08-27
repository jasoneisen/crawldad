using Crawldad.Portal.Auth;
using Crawldad.Portal.Billing;
using Crawldad.Portal.Components;
using Crawldad.Portal.Runs;
using Crawldad.Portal.Tenancy;
using Marten;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace Crawldad.Portal;

/// <summary>All host wiring for the portal lives here (not in Program.cs) so that booting through
/// WebApplicationFactory in the tests exercises — and therefore covers — every line of configuration. The portal
/// is a self-contained Blazor SSR host: it does not reference the API, and its Marten store is isolated in the
/// "portal" schema.</summary>
public static class PortalHost
{
    public static WebApplicationBuilder AddCrawldadPortal(this WebApplicationBuilder builder)
    {
        // The portal's OWN Marten store on the shared Postgres. Connection-string convention mirrors the API
        // ("marten"); DatabaseSchemaName "portal" keeps it fully isolated from the API's "crawldad" schema.
        var marten = builder.Services.AddMarten(options =>
        {
            options.Connection(builder.Configuration.GetConnectionString("marten")!);
            options.DatabaseSchemaName = "portal";

            // Email is the account identity → unique by construction, and case-insensitive because we always
            // store it lower-invariant.
            options.Schema.For<PortalUser>().Identity(u => u.Email);
            // The tenant link shares that identity (same normalized email), so a user and their link line up 1:1.
            options.Schema.For<PortalTenantLink>().Identity(l => l.Email);

            // Optimistic concurrency on the challenge: parallel verify attempts serialize on the document version,
            // so the per-challenge attempt cap can't be beaten by racing guesses (see PortalAuthService.VerifyCodeAsync).
            options.Schema.For<OtpChallenge>().UseOptimisticConcurrency(true);
        });

        // Dev convenience only: diff/apply the schema on boot. Prod/CI would apply it out-of-band.
        if (builder.Environment.IsDevelopment())
        {
            marten.ApplyAllDatabaseChangesOnStartup();
        }

        // Time is a DI seam — the integration tests swap in a controllable clock to drive code expiry.
        builder.Services.AddSingleton(TimeProvider.System);

        // Passwordless email-OTP services.
        builder.Services.AddSingleton<IOtpCodeGenerator, OtpCodeGenerator>();
        builder.Services.AddScoped<IPortalAuthService, PortalAuthService>();
        AddEmailSender(builder);
        AddTenantLinking(builder);

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
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
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

    // Portal → Crawldad tenant link + the per-request typed API client (Crawldad.Client). The signed-in user's link
    // (normalized email → tenant id + Data-Protection-encrypted API key) is resolved per request by
    // IPortalTenantContext, which hands the data pages a CrawldadClient authenticated as that tenant — or a clean
    // NotLinkedException when the request is unauthenticated or the account has no link.
    private static void AddTenantLinking(WebApplicationBuilder builder)
    {
        // Data Protection is registered explicitly (also implied by antiforgery/cookies) so the at-rest key cipher is
        // unambiguous; the durable key ring for production (mirroring the API's DataProtectionModule) is a later
        // deployment concern, tracked separately.
        builder.Services.AddDataProtection();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IPortalTenantLinkStore, MartenPortalTenantLinkStore>();
        builder.Services.AddScoped<IPortalTenantContext, PortalTenantContext>();

        // The circuit-safe resolver the interactive live-trace page uses: same link → same tenant client as the
        // per-request context, but sourced from AuthenticationStateProvider instead of the (circuit-null) HttpContext.
        builder.Services.AddScoped<ICircuitTenantResolver, CircuitTenantResolver>();

        // The account area's real link-creation path: validates a submitted key against the live API before persisting,
        // so a wrong key is never stored. Stateless over the store + the same pooled API HttpClient.
        builder.Services.AddScoped<IWorkspaceLinker, WorkspaceLinker>();

        // One pooled HttpClient the SDK rides on, base address from config (validated at boot so a missing/malformed
        // URL fails loudly here, not on the first API call). The per-request API key is applied by the context — the
        // client is never registered with a baked-in or empty key.
        var apiBaseUrl = PortalTenancy.ResolveApiBaseUrl(builder.Configuration);
        builder.Services.AddHttpClient(PortalTenancy.ApiHttpClientName, client => client.BaseAddress = apiBaseUrl);

        // Development-only: seed/refresh one tenant link from Portal:DevTenantLink at startup. Registered AFTER
        // Marten's schema-apply-on-startup, so the "portal" tables exist when it writes; a no-op when the section is
        // absent or partial (production boots with no link, exactly as here).
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.Configure<DevTenantLinkOptions>(builder.Configuration.GetSection(PortalTenancy.DevTenantLinkSection));
            builder.Services.AddHostedService<DevTenantLinkSeeder>();
        }
    }

    // Development logs the code (LoggingEmailSender). Every other environment fails CLOSED with a sender that
    // refuses to send — it must never silently succeed nor log a real code. A real provider replaces this later.
    private static void AddEmailSender(WebApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            builder.Services.AddSingleton<IEmailSender, UnconfiguredEmailSender>();
        }
    }
}
