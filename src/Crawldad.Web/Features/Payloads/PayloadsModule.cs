using Crawldad.Contracts.Payloads;
using FluentValidation;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// Self-registration for the Payloads slice (§14.1/§14.4): the event-sourced <see cref="Payload"/> aggregate whose
/// stream is its version history, plus the slice's DI (the <c>POST /payloads</c> boundary validator). This work
/// package ships draft-at-save with validation; revise/rename/archive, the <c>PayloadSummary</c> read model, and drift
/// arrive in Phase 5, filling this module in place.
/// </summary>
public static class PayloadsModule
{
    /// <summary>Registers the <see cref="Payload"/> snapshot on the shared <paramref name="lifecycle"/> (Inline under the test switch, Async in production).</summary>
    /// <param name="options">The Marten store options.</param>
    /// <param name="lifecycle">The shared projection lifecycle.</param>
    public static void ConfigureMarten(StoreOptions options, ProjectionLifecycle lifecycle)
    {
        // Both enums declare Inline=0, Async=1, so the ordinal cast faithfully maps the config-driven lifecycle
        // (mirrors RunsModule). PayloadDrafted carries a Payload.Create Apply, so Marten discovers it from the snapshot.
        options.Projections.Snapshot<Payload>((SnapshotLifecycle)(int)lifecycle);
    }

    /// <summary>Registers the slice's services: the <c>POST /payloads</c> boundary validator.</summary>
    /// <param name="services">The DI container.</param>
    public static void AddPayloadsServices(IServiceCollection services) =>
        services.AddScoped<IValidator<SavePayloadRequest>, SavePayloadRequestValidator>();
}
