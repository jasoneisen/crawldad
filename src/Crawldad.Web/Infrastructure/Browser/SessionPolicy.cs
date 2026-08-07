using System.Text.Json;

namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>What the §8.1 route policy decides for one intercepted request (the three branches of the reference's
/// <c>PlaywrightFactory</c> route handler).</summary>
public enum RouteDisposition
{
    /// <summary>Abort the request — a blocked host or a blocked resource type (image/media/font/analytics).</summary>
    Block,

    /// <summary>Serve from (or populate) the cross-run asset cache — a cacheable resource type or URL suffix.</summary>
    Cache,

    /// <summary>Let the request through the global serialized throttle, then continue to the origin.</summary>
    PassThrough,
}

/// <summary>
/// The §8.1 request-interception policy (the reference's <c>PlaywrightFactory</c> route block): abort by host <b>or</b>
/// resource type; else cache stylesheet/script/<c>.html</c>/<c>.js</c>; else throttle through one global tick.
/// <see cref="Classify"/> is the pure decision the real <c>page.RouteAsync</c> handler drives (§9.2).
/// </summary>
/// <param name="BlockHosts">Hosts whose requests are aborted (analytics/CDN noise the reference blocks).</param>
/// <param name="BlockResourceTypes">Playwright resource types aborted wholesale (<c>image</c>/<c>media</c>/<c>font</c>).</param>
/// <param name="CacheResourceTypes">Resource types served from the cross-run asset cache (<c>stylesheet</c>/<c>script</c>).</param>
/// <param name="CacheUrlSuffixes">URL suffixes served from the cache regardless of resource type (<c>.html</c>/<c>.js</c>).</param>
/// <param name="ThrottleMinIntervalMs">Minimum spacing between two non-cached requests, globally serialized; 0 disables throttling.</param>
public sealed record RoutePolicy(
    IReadOnlySet<string> BlockHosts,
    IReadOnlySet<string> BlockResourceTypes,
    IReadOnlySet<string> CacheResourceTypes,
    IReadOnlyList<string> CacheUrlSuffixes,
    int ThrottleMinIntervalMs)
{
    /// <summary>A no-op route policy: nothing blocked, nothing cached, no throttle. The default when <c>config.route</c> is absent.</summary>
    public static RoutePolicy None { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        [],
        0);

    /// <summary>
    /// Decides how one request is handled, reproducing the reference's order exactly: block (host or resource type)
    /// wins over cache, which wins over pass-through. Matches <c>PlaywrightFactory.CreateBrowserContext</c>'s route.
    /// </summary>
    /// <param name="url">The absolute request URL (its host and suffix are read here).</param>
    /// <param name="resourceType">The Playwright request resource type (<c>document</c>/<c>stylesheet</c>/<c>image</c>/…).</param>
    /// <returns>The disposition the route handler applies.</returns>
    public RouteDisposition Classify(string url, string resourceType)
    {
        var host = new Uri(url).Host;
        if (BlockHosts.Contains(host) || BlockResourceTypes.Contains(resourceType))
        {
            return RouteDisposition.Block;
        }

        if (CacheResourceTypes.Contains(resourceType) || HasCacheableSuffix(url))
        {
            return RouteDisposition.Cache;
        }

        return RouteDisposition.PassThrough;
    }

    private bool HasCacheableSuffix(string url)
    {
        foreach (var suffix in CacheUrlSuffixes)
        {
            if (url.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The §8.1 session config the interpreter builds from a payload's <c>config</c> and hands to <see cref="IBrowserBackend.ConnectAsync"/>
/// (§9.2): launch args, context <c>bypassCsp</c>, the default timeout, and the <see cref="RoutePolicy"/>. A real adapter
/// applies the launch/context options at connect and the route policy per new page; the record/replay fake ignores it.
/// All keys are optional — an omitted key takes its §8.1 default so a config carrying only <c>backend</c> yields a
/// no-op policy.
/// </summary>
/// <param name="LaunchArgs">Chromium launch args passed to a launching backend (e.g. <c>--disable-web-security</c>); empty when <c>config.launch</c> is absent.</param>
/// <param name="BypassCsp">Whether the created context bypasses Content-Security-Policy (<c>BypassCSP</c>); false when absent.</param>
/// <param name="DefaultTimeoutMs">The context default timeout and the interpreter's timeout-hierarchy floor (§8.4); 120000 when absent.</param>
/// <param name="Route">The request-interception policy applied via <c>page.RouteAsync</c> (§9.2).</param>
public sealed record SessionPolicy(
    IReadOnlyList<string> LaunchArgs,
    bool BypassCsp,
    int DefaultTimeoutMs,
    RoutePolicy Route)
{
    /// <summary>The default policy (no launch args, no CSP bypass, 120 s timeout, no routing) — used where no config drives it (the fake test seam).</summary>
    public static SessionPolicy Default { get; } = new([], false, 120000, RoutePolicy.None);

    /// <summary>
    /// Builds the policy from a payload's <c>config</c> element (§8.1). Each block is optional and defaults per §8.1;
    /// the acceptance payloads' full launch/context/route blocks map straight through.
    /// </summary>
    /// <param name="config">The payload's <c>config</c> object.</param>
    /// <returns>The parsed session policy.</returns>
    public static SessionPolicy FromConfig(JsonElement config)
    {
        var launchArgs = config.TryGetProperty("launch", out var launch) && launch.TryGetProperty("args", out var args)
            ? args.EnumerateArray().Select(static a => a.GetString()!).ToArray()
            : [];

        var bypassCsp = config.TryGetProperty("context", out var context)
            && context.TryGetProperty("bypassCsp", out var csp)
            && csp.GetBoolean();

        var defaultTimeoutMs = config.TryGetProperty("defaultTimeoutMs", out var t) ? t.GetInt32() : 120000;

        var route = config.TryGetProperty("route", out var routeElement) ? ReadRoute(routeElement) : RoutePolicy.None;

        return new SessionPolicy(launchArgs, bypassCsp, defaultTimeoutMs, route);
    }

    private static RoutePolicy ReadRoute(JsonElement route)
    {
        var throttle = route.TryGetProperty("throttle", out var th) && th.TryGetProperty("minIntervalMs", out var mi)
            ? mi.GetInt32()
            : 0;

        return new RoutePolicy(
            ReadSet(route, "blockHosts"),
            ReadSet(route, "blockResourceTypes"),
            ReadSet(route, "cacheResourceTypes"),
            route.TryGetProperty("cacheUrlSuffixes", out var suffixes)
                ? suffixes.EnumerateArray().Select(static s => s.GetString()!).ToArray()
                : [],
            throttle);
    }

    private static HashSet<string> ReadSet(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var array)
            ? array.EnumerateArray().Select(static e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
}
