using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>Resolves a <c>storageTarget</c>'s <c>kind</c> to the <see cref="IDownloadSink"/> that handles it. A
/// payload's <c>download.to</c> evaluates to a <c>Target</c> object (<c>{ "kind": "fake", "name": "attachmentStore" }</c>);
/// the engine selects by kind (data, not a hard-coded type). An unknown kind is a terminal <c>unknown_download_sink</c> failure.</summary>
public interface IDownloadSinkRegistry
{
    /// <summary>Resolves the sink for a target <paramref name="kind"/>; <see langword="true"/> when one is registered.</summary>
    bool TryResolve(string kind, [NotNullWhen(true)] out IDownloadSink? sink);
}

/// <summary>Registry over .NET keyed services: each sink kind is a keyed <see cref="IDownloadSink"/> (key = <c>kind</c>),
/// so a new sink is one <c>AddKeyedSingleton</c> line with no change here.</summary>
internal sealed class KeyedDownloadSinkRegistry(IServiceProvider services) : IDownloadSinkRegistry
{
    public bool TryResolve(string kind, [NotNullWhen(true)] out IDownloadSink? sink)
    {
        sink = services.GetKeyedService<IDownloadSink>(kind);
        return sink is not null;
    }
}
