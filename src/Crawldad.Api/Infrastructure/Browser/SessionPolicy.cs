using System.Text.Json;
using Crawldad.Api.Features.Runs.Interpreter;

namespace Crawldad.Api.Infrastructure.Browser;

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
        // config is a validated object here (the run-time pre-pass guarantees it); each sub-key stays optional, but a
        // present one of the wrong JSON kind classifies as malformed_node rather than throwing from a raw accessor.
        IReadOnlyList<string> launchArgs = NodeJson.OptionalObject(config, "launch") is { } launch
            ? NodeJson.OptionalStringArray(launch, "args")
            : [];

        var bypassCsp = NodeJson.OptionalObject(config, "context") is { } context
            && NodeJson.OptionalBool(context, "bypassCsp", false);

        var defaultTimeoutMs = NodeJson.OptionalInt(config, "defaultTimeoutMs", 120000);

        var route = NodeJson.OptionalObject(config, "route") is { } routeElement ? ReadRoute(routeElement) : RoutePolicy.None;

        return new SessionPolicy(launchArgs, bypassCsp, defaultTimeoutMs, route);
    }

    private static RoutePolicy ReadRoute(JsonElement route)
    {
        var throttle = NodeJson.OptionalObject(route, "throttle") is { } th ? NodeJson.OptionalInt(th, "minIntervalMs", 0) : 0;

        return new RoutePolicy(
            new HashSet<string>(NodeJson.OptionalStringArray(route, "blockHosts"), StringComparer.Ordinal),
            new HashSet<string>(NodeJson.OptionalStringArray(route, "blockResourceTypes"), StringComparer.Ordinal),
            new HashSet<string>(NodeJson.OptionalStringArray(route, "cacheResourceTypes"), StringComparer.Ordinal),
            NodeJson.OptionalStringArray(route, "cacheUrlSuffixes"),
            throttle);
    }
}
