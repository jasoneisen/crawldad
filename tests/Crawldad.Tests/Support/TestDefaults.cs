using Crawldad.Api;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Wolverine;

namespace Crawldad.Tests.Support;

/// <summary>The determinism core every integration host shares: Inline projections need no async-daemon wait, an
/// isolated Marten schema keeps parallel test classes from racing, and Marten + Wolverine run single-node. Call
/// sites layer their own extras on top (a fake clock, a Production environment).</summary>
public static class TestDefaults
{
    public static IWebHostBuilder UseCrawldadTestDefaults(this IWebHostBuilder builder, string schemaName)
    {
        builder.UseSetting(HostConfiguration.ProjectionLifecycleKey, "Inline");

        // Select the in-memory blob provider: the fake download sink + in-memory screenshot store, so the hermetic
        // suite runs with no filesystem or emulator dependency. The durable filesystem/Azure adapters are covered by
        // their own tests; a call site that wants the filesystem provider overrides this key.
        builder.UseSetting("Crawldad:Storage:Provider", "fake");

        // Configure the tenant directory so the host's registry admits the test tenants. The Alba host layers the
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

            // Sub-second durability cadence for the TEST HOST ONLY; production keeps Wolverine's 5 s default
            // (HostConfiguration.ConfigureWolverine is untouched). A missed in-process delivery is otherwise only
            // recovered by the slower backstop poll, which can stack across the promotion pipeline and flake test timeouts.
            services.ConfigureWolverine(options =>
            {
                options.Durability.ScheduledJobFirstExecution = TimeSpan.FromMilliseconds(100);
                options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(250);
            });
        });
        return builder;
    }
}
