using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Api.Infrastructure.Browser;

/// <summary>Resolves a <see cref="BackendBinding.Adapter"/> id to the <see cref="IBrowserBackend"/> that handles it,
/// so the interpreter selects a backend by data rather than a hard-coded type. An unknown adapter is a terminal
/// <c>unknown_backend_adapter</c> failure, surfaced by the caller.</summary>
public interface IBrowserBackendRegistry
{
    /// <summary>Resolves the backend for <paramref name="adapter"/>; false when none is registered.</summary>
    bool TryResolve(string adapter, [NotNullWhen(true)] out IBrowserBackend? backend);
}

/// <summary>Registry over .NET keyed services: each adapter is registered as a keyed <see cref="IBrowserBackend"/>
/// (key = adapter id), so adding a new adapter is one <c>AddKeyedSingleton</c> line with no change here.</summary>
internal sealed class KeyedBrowserBackendRegistry(IServiceProvider services) : IBrowserBackendRegistry
{
    public bool TryResolve(string adapter, [NotNullWhen(true)] out IBrowserBackend? backend)
    {
        backend = services.GetKeyedService<IBrowserBackend>(adapter);
        return backend is not null;
    }
}
