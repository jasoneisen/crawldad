using System.Collections.Generic;
using Crawldad.Web.Infrastructure.Browser.Real;

namespace Crawldad.Tests.Unit;

/// <summary>The <see cref="BrowserlessBackend.BuildEndpoint"/> connect URL: the region substitutes into the
/// template, the token is the first query param (URL-escaped), and every other non-null <c>backendOptions</c> entry
/// (excluding <c>region</c>) is appended, ordinally sorted, with booleans lower-cased and other scalars JSON-serialized.</summary>
public class BrowserlessEndpointTests
{
    [Fact]
    public void Builds_the_production_url_with_no_options()
    {
        BrowserlessBackend.BuildEndpoint(BrowserlessBackend.DefaultEndpointTemplate, "sfo", "tok123", null)
            .ShouldBe("wss://production-sfo.browserless.io/chromium/playwright?token=tok123");
    }

    [Fact]
    public void Substitutes_the_region_and_escapes_the_token()
    {
        BrowserlessBackend.BuildEndpoint(BrowserlessBackend.DefaultEndpointTemplate, "lon", "a b/c", null)
            .ShouldBe("wss://production-lon.browserless.io/chromium/playwright?token=a%20b%2Fc");
    }

    [Fact]
    public void Appends_passthrough_options_sorted_excluding_region_and_nulls()
    {
        var options = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["region"] = "sfo",     // excluded — it selects the datacenter, not a query param
            ["proxy"] = "residential",
            ["blockAds"] = true,    // bool → lower-case
            ["headful"] = false,    // bool → lower-case
            ["ttl"] = 60L,          // number → JSON-serialized
            ["dropped"] = null,     // null → skipped
        };

        BrowserlessBackend.BuildEndpoint("wss://host/path", "sfo", "T", options)
            .ShouldBe("wss://host/path?token=T&blockAds=true&headful=false&proxy=residential&ttl=60");
    }
}
