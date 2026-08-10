using System.Text.Json;

namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>What the route policy decides for one intercepted request: block, cache, or pass through.</summary>
public enum RouteDisposition
{
    /// <summary>Abort the request — a blocked host or a blocked resource type (image/media/font/analytics).</summary>
    Block,

    /// <summary>Serve from (or populate) the cross-run asset cache — a cacheable resource type or URL suffix.</summary>
    Cache,

    /// <summary>Let the request through the global serialized throttle, then continue to the origin.</summary>
    PassThrough,
}

/// <summary>The request-interception policy: abort by host or resource type; else cache stylesheet/script/<c>.html</c>/
/// <c>.js</c>; else throttle through one global tick. <see cref="Classify"/> is the pure decision <c>page.RouteAsync</c>
/// drives.</summary>
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

    /// <summary>Decides how one request is handled: block (host or resource type) wins over cache, which wins over
    /// pass-through.</summary>
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

/// <summary>The session config the interpreter builds from a payload's <c>config</c> and hands to
/// <see cref="IBrowserBackend.ConnectAsync"/>: launch args, context <c>bypassCsp</c>, the default timeout, and the
/// <see cref="RoutePolicy"/>. All keys are optional — an omitted key takes its default.</summary>
public sealed record SessionPolicy(
    IReadOnlyList<string> LaunchArgs,
    bool BypassCsp,
    int DefaultTimeoutMs,
    RoutePolicy Route)
{
    /// <summary>The default policy (no launch args, no CSP bypass, 120 s timeout, no routing) — used where no config drives it (the fake test seam).</summary>
    public static SessionPolicy Default { get; } = new([], false, 120000, RoutePolicy.None);

    /// <summary>Builds the policy from a payload's <c>config</c> element. Each block is optional and defaults
    /// accordingly.</summary>
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
