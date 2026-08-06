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
