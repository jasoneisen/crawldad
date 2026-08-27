using Crawldad.Portal.Auth;
using Crawldad.Portal.Components;
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
        // The portal's OWN Marten store on the shared Postgres. Connection-string convention mirrors the API
        // ("marten"); DatabaseSchemaName "portal" keeps it fully isolated from the API's "crawldad" schema.
        var marten = builder.Services.AddMarten(options =>
        {
            options.Connection(builder.Configuration.GetConnectionString("marten")!);
            options.DatabaseSchemaName = "portal";

            // Email is the account identity → unique by construction, and case-insensitive because we always
            // store it lower-invariant.
            options.Schema.For<PortalUser>().Identity(u => u.Email);
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

        // Cookie authentication — the ASP.NET cookie handler directly, no ASP.NET Identity framework.
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

        // Blazor Web App — Interactive Server render mode ONLY (no WebAssembly).
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

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
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
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
