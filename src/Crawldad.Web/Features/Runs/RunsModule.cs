using Crawldad.Contracts.Runs;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using FluentValidation;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// Self-registration for the Runs slice (§14.2/§14.4): the <see cref="Run"/> aggregate snapshot on the shared
/// projection lifecycle, plus the slice's DI (the <c>POST /runs</c> validator and the browser-backend seam). Phase 5
/// adds the executor saga, step-level trace events, and the RunTimeline read model here.
/// </summary>
public static class RunsModule
{
    /// <summary>Registers the <see cref="Run"/> snapshot on the shared <paramref name="lifecycle"/> (Inline under the test switch, Async in production).</summary>
    /// <param name="options">The Marten store options.</param>
    /// <param name="lifecycle">The shared projection lifecycle.</param>
    public static void ConfigureMarten(StoreOptions options, ProjectionLifecycle lifecycle) =>
        // Both enums declare Inline=0, Async=1 (SnapshotLifecycle has no Live, which P1 never selects), so the
        // ordinal cast is a branch-free, faithful mapping from the config-driven ProjectionLifecycle.
        options.Projections.Snapshot<Run>((SnapshotLifecycle)(int)lifecycle);

    /// <summary>Registers the slice's services: the request validator and the browser-backend registry + P1 fake.</summary>
    /// <param name="services">The DI container.</param>
    public static void AddRunsServices(IServiceCollection services)
    {
        services.AddScoped<IValidator<StartRunRequest>, StartRunRequestValidator>();

        // The backend seam: a registry over keyed adapters. Phase 1 registers only the record/replay fake, reading
        // shipped fixtures from the app's output directory; Phase 4 adds real adapters with more AddKeyedSingleton lines.
        services.AddSingleton<IBrowserBackendRegistry, KeyedBrowserBackendRegistry>();
        services.AddKeyedSingleton<IBrowserBackend>(
            "fake",
            static (_, _) => new FakeBrowserBackend(Path.Combine(AppContext.BaseDirectory, "Fixtures")));
    }
}
