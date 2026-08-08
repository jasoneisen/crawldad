using Crawldad.Web;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Wolverine;

namespace Crawldad.Tests.Support;

/// <summary>
/// The determinism core every integration host shares: projections run Inline (via the single
/// <see cref="HostConfiguration.ProjectionLifecycleKey"/>) so assertions need no async-daemon wait, an isolated
/// Marten schema keeps parallel test classes from racing, and both Marten and Wolverine run single-node. Call
/// sites layer their own deliberate extras on top (a fake clock, a Production environment).
/// </summary>
public static class TestDefaults
{
    public static IWebHostBuilder UseCrawldadTestDefaults(this IWebHostBuilder builder, string schemaName)
    {
        builder.UseSetting(HostConfiguration.ProjectionLifecycleKey, "Inline");

        // Select the in-memory blob provider (CD-2): the fake download sink + in-memory screenshot store, so the hermetic
        // suite runs with no filesystem or emulator dependency and every existing test — which drives the "fake" download
        // kind and casts IScreenshotStore to InMemoryScreenshotStore — stays byte-identical. The durable filesystem/Azure
        // adapters are covered by their own tests; a call site that wants the filesystem provider overrides this key.
        builder.UseSetting("Crawldad:Storage:Provider", "fake");

        // Configure the tenant directory (CD-1) so the host's registry admits the test tenants. The Alba host layers the
        // primary tenant's key onto every scenario (TestTenants.AuthenticatedAsPrimaryTenant) so the request-based suite
        // stays green; the cross-tenant and unauthenticated tests override the header per scenario.
        foreach (var (key, value) in TestTenants.Configuration)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.ConfigureMarten(options => options.DatabaseSchemaName = schemaName);
            services.MartenDaemonModeIsSolo();
            services.DisableAllExternalWolverineTransports();
            services.RunWolverineInSoloMode();
        });
        return builder;
    }
}
