using System.Collections.Generic;
using Crawldad.Web.Infrastructure.Browser.Real;

namespace Crawldad.Tests.Unit;

/// <summary>The cross-run <see cref="InMemoryAssetCache"/>: a first lookup is a miss (fetches once), a second is a
/// hit (no fetch); entries are region-scoped so the same URL in two regions is two independent misses.</summary>
public class AssetCacheTests
{
    private static readonly IEnumerable<KeyValuePair<string, string>> _noHeaders = [];

    [Fact]
    public async Task Miss_then_hit_for_the_same_region_and_url()
    {
        var cache = new InMemoryAssetCache();
        var fetches = 0;
        Task<CachedAsset> Fetch()
        {
            fetches++;
            return Task.FromResult(new CachedAsset([1, 2, 3], _noHeaders, 200));
        }

        var first = await cache.GetOrAddAsync("sfo", "https://x/app.css", Fetch);
        first.Hit.ShouldBeFalse();
        first.Asset.Body.ShouldBe([1, 2, 3]);

        var second = await cache.GetOrAddAsync("sfo", "https://x/app.css", Fetch);
        second.Hit.ShouldBeTrue();
        fetches.ShouldBe(1);
    }

    [Fact]
    public async Task Entries_are_region_scoped()
    {
        var cache = new InMemoryAssetCache();
        var fetches = 0;
        Task<CachedAsset> Fetch()
        {
            fetches++;
            return Task.FromResult(new CachedAsset([9], _noHeaders, 200));
        }

        (await cache.GetOrAddAsync("sfo", "https://x/a.js", Fetch)).Hit.ShouldBeFalse();
        (await cache.GetOrAddAsync("lon", "https://x/a.js", Fetch)).Hit.ShouldBeFalse();
        fetches.ShouldBe(2);
    }
}
