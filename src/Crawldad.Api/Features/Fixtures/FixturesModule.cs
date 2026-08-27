using Crawldad.Api.Infrastructure.Browser;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Api.Features.Fixtures;

/// <summary>Self-registration for the Fixtures slice: the tenant-scoped <see cref="FixtureSet"/> document, the fixture
/// store, and the <c>fixture</c> replay backend adapter. Endpoints are auto-discovered by Wolverine.Http; the record
/// endpoint reuses the Runs slice's interpreter + backend/sink seams to execute the recording run.</summary>
public static class FixturesModule
{
    /// <summary>Registers the tenant-scoped fixture-set document (a plain Marten doc, tenant-qualified by the shared
    /// <c>AllDocumentsAreMultiTenanted</c> policy — no projection).</summary>
    public static void ConfigureMarten(StoreOptions options) => options.Schema.For<FixtureSet>();

    /// <summary>Registers the fixture store and the tenant fixture <b>replay</b> backend (adapter id <c>"fixture"</c>),
    /// resolved by the same keyed-adapter registry the Runs slice wires the <c>fake</c>/real backends into.</summary>
    public static void AddFixturesServices(IServiceCollection services)
    {
        services.AddSingleton<IFixtureStore, MartenFixtureStore>();
        services.AddKeyedSingleton<IBrowserBackend>(
            "fixture",
            static (sp, _) => new TenantFixtureBackend(sp.GetRequiredService<IFixtureStore>()));
    }
}
