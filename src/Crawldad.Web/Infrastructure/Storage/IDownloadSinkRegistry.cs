using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// Resolves a <c>storageTarget</c>'s <c>kind</c> to the <see cref="IDownloadSink"/> that handles it — the download
/// analogue of <c>IBrowserBackendRegistry</c>. A payload's <c>download.to</c> evaluates to a <c>Target</c> object
/// (<c>{ "kind": "fake", "name": "attachmentStore" }</c> in Phase 2); the engine selects the sink by that <c>kind</c>
/// (data, not a hard-coded type), so Phase 4 adds a <c>presignedUrl</c> / <c>blobStore</c> kind with one
/// <c>AddKeyedSingleton</c> line. An unknown kind is a terminal <c>unknown_download_sink</c> failure.
/// </summary>
public interface IDownloadSinkRegistry
{
    /// <summary>Resolves the sink for a target <paramref name="kind"/>.</summary>
    /// <param name="kind">The <c>storageTarget.kind</c> from the payload's resolved <c>download.to</c>.</param>
    /// <param name="sink">The resolved sink when the kind is registered.</param>
    /// <returns><see langword="true"/> when a sink is registered for <paramref name="kind"/>; otherwise <see langword="false"/>.</returns>
    bool TryResolve(string kind, [NotNullWhen(true)] out IDownloadSink? sink);
}

/// <summary>
/// Registry over .NET keyed services: each sink kind is a keyed <see cref="IDownloadSink"/> (key = <c>kind</c>), so a
/// Phase 4 sink is one <c>AddKeyedSingleton</c> line with no change here.
/// </summary>
internal sealed class KeyedDownloadSinkRegistry(IServiceProvider services) : IDownloadSinkRegistry
{
    public bool TryResolve(string kind, [NotNullWhen(true)] out IDownloadSink? sink)
    {
        sink = services.GetKeyedService<IDownloadSink>(kind);
        return sink is not null;
    }
}
