using System.Text.Json;
using Crawldad.Api.Infrastructure.Browser;

namespace Crawldad.Tests.Unit;

/// <summary>The <see cref="SessionPolicy"/> parsed from a payload's <c>config</c>, and the <see cref="RoutePolicy.Classify"/>
/// decision that drives the real route handler. Pure — no browser — so every parse and classify branch is covered here.</summary>
public class SessionPolicyTests
{
    private static JsonElement Config(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void FromConfig_reads_the_full_acceptance_block()
    {
        var policy = SessionPolicy.FromConfig(Config(
            """
            {
              "backend": "input.backend",
              "defaultTimeoutMs": 90000,
              "launch": { "args": ["--disable-web-security", "--no-sandbox"] },
              "context": { "bypassCsp": true },
              "route": {
                "blockHosts": ["cdn.walkme.com"],
                "blockResourceTypes": ["image", "font"],
                "cacheResourceTypes": ["stylesheet", "script"],
                "cacheUrlSuffixes": [".html", ".js"],
                "throttle": { "minIntervalMs": 2000 }
              }
            }
            """));

        policy.LaunchArgs.ShouldBe(["--disable-web-security", "--no-sandbox"]);
        policy.BypassCsp.ShouldBeTrue();
        policy.DefaultTimeoutMs.ShouldBe(90000);
        policy.Route.BlockHosts.ShouldBe(["cdn.walkme.com"]);
        policy.Route.BlockResourceTypes.ShouldContain("image");
        policy.Route.CacheResourceTypes.ShouldContain("stylesheet");
        policy.Route.CacheUrlSuffixes.ShouldBe([".html", ".js"]);
        policy.Route.ThrottleMinIntervalMs.ShouldBe(2000);
    }

    [Fact]
    public void FromConfig_applies_defaults_when_blocks_are_absent()
    {
        var policy = SessionPolicy.FromConfig(Config("""{ "backend": "input.backend" }"""));

        policy.LaunchArgs.ShouldBeEmpty();
        policy.BypassCsp.ShouldBeFalse();
        policy.DefaultTimeoutMs.ShouldBe(120000);
        policy.Route.BlockHosts.ShouldBeEmpty();
        policy.Route.BlockResourceTypes.ShouldBeEmpty();
        policy.Route.CacheResourceTypes.ShouldBeEmpty();
        policy.Route.CacheUrlSuffixes.ShouldBeEmpty();
        policy.Route.ThrottleMinIntervalMs.ShouldBe(0);
    }

    [Fact]
    public void FromConfig_handles_present_but_empty_sub_blocks()
    {
        // launch/context present without their leaf keys; route present without its lists and with an empty throttle.
        var policy = SessionPolicy.FromConfig(Config(
            """{ "backend": "b", "launch": {}, "context": {}, "route": { "throttle": {} } }"""));

        policy.LaunchArgs.ShouldBeEmpty();
        policy.BypassCsp.ShouldBeFalse();
        policy.Route.BlockHosts.ShouldBeEmpty();
        policy.Route.CacheUrlSuffixes.ShouldBeEmpty();
        policy.Route.ThrottleMinIntervalMs.ShouldBe(0);
    }

    [Fact]
    public void Default_is_a_no_op_policy()
    {
        SessionPolicy.Default.LaunchArgs.ShouldBeEmpty();
        SessionPolicy.Default.BypassCsp.ShouldBeFalse();
        SessionPolicy.Default.DefaultTimeoutMs.ShouldBe(120000);
        SessionPolicy.Default.Route.ShouldBe(RoutePolicy.None);
    }

    [Fact]
    public void Classify_blocks_by_host()
    {
        var route = new RoutePolicy(
            new HashSet<string>(StringComparer.Ordinal) { "cdn.walkme.com" },
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            [],
            0);

        route.Classify("https://cdn.walkme.com/widget.js", "script").ShouldBe(RouteDisposition.Block);
    }

    [Fact]
    public void Classify_blocks_by_resource_type_when_the_host_is_allowed()
    {
        var route = new RoutePolicy(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "image" },
            new HashSet<string>(StringComparer.Ordinal),
            [],
            0);

        route.Classify("https://site.example/logo.png", "image").ShouldBe(RouteDisposition.Block);
    }

    [Fact]
    public void Classify_caches_by_resource_type_and_by_url_suffix()
    {
        var route = new RoutePolicy(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "stylesheet" },
            [".js"],
            0);

        route.Classify("https://site.example/app.css", "stylesheet").ShouldBe(RouteDisposition.Cache); // by resource type
        route.Classify("https://site.example/app.js", "other").ShouldBe(RouteDisposition.Cache);        // by URL suffix
    }

    [Fact]
    public void Classify_passes_through_everything_else()
    {
        RoutePolicy.None.Classify("https://site.example/CapDetail.aspx", "document").ShouldBe(RouteDisposition.PassThrough);
    }

    [Fact]
    public void Classify_passes_through_a_url_matching_no_cache_suffix()
    {
        // Two suffixes to scan, neither matching ⇒ the suffix loop iterates to exhaustion, then falls through.
        var route = new RoutePolicy(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            [".html", ".js"],
            0);

        route.Classify("https://site.example/data.json", "xhr").ShouldBe(RouteDisposition.PassThrough);
    }
}
