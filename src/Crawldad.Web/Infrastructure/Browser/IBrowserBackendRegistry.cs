using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>
/// Resolves a <see cref="BackendBinding.Adapter"/> id to the <see cref="IBrowserBackend"/> that handles it. Phase 1
/// registers only <c>"fake"</c>; Phase 4 adds <c>"browserless"</c>/<c>"browserbase"</c>/self-hosted beside it, so
/// the interpreter selects a backend by data (the payload's <c>config.backend.adapter</c>) rather than a hard-coded
/// type. An unknown adapter is a terminal <c>unknown_backend_adapter</c> failure, surfaced by the caller.
/// </summary>
public interface IBrowserBackendRegistry
{
    /// <summary>Resolves the backend for <paramref name="adapter"/>.</summary>
    /// <param name="adapter">The adapter id from the run's <see cref="BackendBinding"/>.</param>
    /// <param name="backend">The resolved backend when the adapter is registered.</param>
    /// <returns><see langword="true"/> when a backend is registered for <paramref name="adapter"/>; otherwise <see langword="false"/>.</returns>
    bool TryResolve(string adapter, [NotNullWhen(true)] out IBrowserBackend? backend);
}

/// <summary>
/// Registry over .NET keyed services: each adapter is registered as a keyed <see cref="IBrowserBackend"/> (key =
/// adapter id), so adding a Phase 4 adapter is one <c>AddKeyedSingleton</c> line with no change here.
/// </summary>
internal sealed class KeyedBrowserBackendRegistry(IServiceProvider services) : IBrowserBackendRegistry
{
    public bool TryResolve(string adapter, [NotNullWhen(true)] out IBrowserBackend? backend)
    {
        backend = services.GetKeyedService<IBrowserBackend>(adapter);
        return backend is not null;
    }
}
