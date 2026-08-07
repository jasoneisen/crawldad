using Crawldad.Contracts.Payloads;
using FluentValidation;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// Self-registration for the Payloads slice (§14.1/§14.4): the event-sourced <see cref="Payload"/> aggregate whose stream
/// is its version history, the async <see cref="PayloadSummaryProjection"/> listing read model, and the slice's DI (the
/// draft/revise/rename boundary validators). Both projections ride the shared, config-driven lifecycle (Inline under the
/// test switch, Async in production), so the read model just lands beside the snapshot exactly as the foundation registers
/// its projections.
/// </summary>
public static class PayloadsModule
{
    /// <summary>Registers the <see cref="Payload"/> snapshot and the <see cref="PayloadSummaryProjection"/> on the shared <paramref name="lifecycle"/>.</summary>
    /// <param name="options">The Marten store options.</param>
    /// <param name="lifecycle">The shared projection lifecycle.</param>
    public static void ConfigureMarten(StoreOptions options, ProjectionLifecycle lifecycle)
    {
        // Both enums declare Inline=0, Async=1, so the ordinal cast faithfully maps the config-driven lifecycle to the
        // snapshot's SnapshotLifecycle (mirrors RunsModule). PayloadDrafted/Revised/Renamed/Archived carry Payload.Create/
        // Apply, so Marten discovers them from the snapshot.
        options.Projections.Snapshot<Payload>((SnapshotLifecycle)(int)lifecycle);

        // The listing read model runs on the same lifecycle directly (Add takes the ProjectionLifecycle) — async in
        // production (cross-payload dashboard, lag fine, §11), inline under the test switch for deterministic assertions.
        options.Projections.Add<PayloadSummaryProjection>(lifecycle);
    }

    /// <summary>Registers the slice's services: the draft/revise/rename boundary validators.</summary>
    /// <param name="services">The DI container.</param>
    public static void AddPayloadsServices(IServiceCollection services)
    {
        services.AddScoped<IValidator<SavePayloadRequest>, SavePayloadRequestValidator>();
        services.AddScoped<IValidator<RevisePayloadRequest>, RevisePayloadRequestValidator>();
        services.AddScoped<IValidator<RenamePayloadRequest>, RenamePayloadRequestValidator>();
    }
}
