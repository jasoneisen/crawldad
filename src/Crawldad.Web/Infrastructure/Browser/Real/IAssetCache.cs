using System.Collections.Concurrent;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>One cached HTTP response body served back to a fulfilled route.</summary>
internal sealed record CachedAsset(byte[] Body, IEnumerable<KeyValuePair<string, string>> Headers, int Status);

/// <summary>The result of a cache lookup: the asset and whether it was already present (a hit) versus just fetched (a miss).</summary>
/// <param name="Asset">The cached (or freshly fetched) asset.</param>
/// <param name="Hit">True when the asset was already cached (no origin fetch happened) — counted into <c>stats.cacheHits</c>.</param>
internal sealed record AssetLookup(CachedAsset Asset, bool Hit);

/// <summary>The cross-run asset cache: serves cacheable assets (stylesheet/script/html/js) from here instead of
/// re-fetching, keyed by <c>(region, url)</c> so the store is region-local. A DI singleton — safe to share across
/// runs because contents are public web assets only, so sharing never crosses the tenant boundary.</summary>
internal interface IAssetCache
{
    /// <summary>Returns the cached asset for <paramref name="url"/> in <paramref name="region"/>, calling
    /// <paramref name="fetch"/> at most once per key on a miss.</summary>
    ValueTask<AssetLookup> GetOrAddAsync(string region, string url, Func<Task<CachedAsset>> fetch);
}

/// <summary>The in-memory <see cref="IAssetCache"/>: a concurrent map keyed by region + URL.</summary>
internal sealed class InMemoryAssetCache : IAssetCache
{
    private readonly ConcurrentDictionary<string, CachedAsset> _entries = new(StringComparer.Ordinal);

    public async ValueTask<AssetLookup> GetOrAddAsync(string region, string url, Func<Task<CachedAsset>> fetch)
    {
        var key = region + "\n" + url;
        if (_entries.TryGetValue(key, out var cached))
        {
            return new AssetLookup(cached, true);
        }

        var fetched = await fetch();
        _entries[key] = fetched;
        return new AssetLookup(fetched, false);
    }
}
