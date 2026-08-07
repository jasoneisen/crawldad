using System.Collections.Concurrent;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>One cached HTTP response body served back to a fulfilled route (the reference's <c>ResponseCache</c>).</summary>
/// <param name="Body">The response body bytes.</param>
/// <param name="Headers">The response headers to replay.</param>
/// <param name="Status">The HTTP status to replay.</param>
internal sealed record CachedAsset(byte[] Body, IEnumerable<KeyValuePair<string, string>> Headers, int Status);

/// <summary>The result of a cache lookup: the asset and whether it was already present (a hit) versus just fetched (a miss).</summary>
/// <param name="Asset">The cached (or freshly fetched) asset.</param>
/// <param name="Hit">True when the asset was already cached (no origin fetch happened) — counted into <c>stats.cacheHits</c>.</param>
internal sealed record AssetLookup(CachedAsset Asset, bool Hit);

/// <summary>
/// The cross-run asset cache (§8.1): the §8.1 route policy serves cacheable assets (stylesheet/script/<c>.html</c>/
/// <c>.js</c>) from here instead of re-fetching, reproducing <c>PlaywrightFactory</c>'s <c>MemoryCache.GetOrCreateAsync</c>.
/// A DI singleton so entries persist across runs (the moat telemetry — "which asset on which site" — accretes for
/// free); keyed by <c>(region, url)</c> so the store is region-local (§8.1/§12). Contents are public web assets only,
/// so cross-run sharing never crosses the tenant boundary (§12).
/// </summary>
internal interface IAssetCache
{
    /// <summary>Returns the cached asset for <paramref name="url"/> in <paramref name="region"/>, populating it via
    /// <paramref name="fetch"/> on a miss.</summary>
    /// <param name="region">The backend region the entry is scoped to.</param>
    /// <param name="url">The absolute asset URL (the cache key within the region).</param>
    /// <param name="fetch">Fetches the asset from the origin on a miss (called at most once per key).</param>
    /// <returns>The asset and whether it was a hit.</returns>
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
