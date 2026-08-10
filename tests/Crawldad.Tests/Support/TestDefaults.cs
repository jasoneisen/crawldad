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

            // Sub-second durability cadence for the TEST HOST ONLY (issue #38). Production keeps Wolverine's 5 s default
            // (HostConfiguration.ConfigureWolverine is untouched). In the happy path every promotion hop (PromoteQueued ->
            // StartRun -> ExecuteRun) is delivered in-process in well under a second; but when a loaded/slow runner starves
            // the thread pool and an in-process flush is missed, the message is only recovered by the durability agent's
            // backstop poll — at the 5 s default (first execution ~1.2 s) that backstop, stacked across the ~4-hop promotion
            // pipeline, can approach the tests' poll windows and time out (the two #38 occurrences). A scheduled QueueWaitDeadline
            // (due at +400 ms) was measured reaching terminal at 0.8–3.8 s purely waiting on this poll. Tightening the cadence
            // collapses the worst-case backstop latency to a fraction of a second, so a missed in-process delivery self-heals
            // promptly rather than sitting behind idle capacity. The polls are lightweight indexed queries, so the higher
            // frequency adds negligible load.
            services.ConfigureWolverine(options =>
            {
                options.Durability.ScheduledJobFirstExecution = TimeSpan.FromMilliseconds(100);
                options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(250);
            });
        });
        return builder;
    }
}
