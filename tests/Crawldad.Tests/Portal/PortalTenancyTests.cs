using Crawldad.Portal.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for the tenancy wiring helpers: the API base-URL config validation (fail-fast at boot).</summary>
public class PortalTenancyTests
{
    private static IConfiguration Config(string? baseUrl)
    {
        var data = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (baseUrl is not null)
        {
            data[PortalTenancy.ApiBaseUrlConfigKey] = baseUrl;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void ResolveApiBaseUrl_returns_the_absolute_uri()
    {
        PortalTenancy.ResolveApiBaseUrl(Config("https://api.crawldad.io/")).AbsoluteUri.ShouldBe("https://api.crawldad.io/");
    }

    [Theory]
    [InlineData(null)]     // unset
    [InlineData("")]       // blank
    [InlineData("   ")]    // whitespace
    [InlineData("api/v1")] // present but not an absolute URL
    public void ResolveApiBaseUrl_rejects_a_missing_or_malformed_url(string? raw)
    {
        Should.Throw<InvalidOperationException>(() => PortalTenancy.ResolveApiBaseUrl(Config(raw)));
    }
}
