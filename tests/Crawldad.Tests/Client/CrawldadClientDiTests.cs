using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Client;

/// <summary>Unit tests for the DI extension, the options validation/normalization, and the client constructor's base
/// address handling.</summary>
public class CrawldadClientDiTests
{
    [Fact]
    public async Task AddCrawldadClient_registers_a_working_typed_client()
    {
        var services = new ServiceCollection();
        services.AddCrawldadClient(options =>
            {
                options.BaseUrl = new Uri("https://api.crawldad.test");
                options.ApiKey = ClientTestHarness.ApiKey;
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new StubHttpMessageHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(1, 2, 3))));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<CrawldadClient>();

        (await client.GetQueueStatsAsync()).Queued.ShouldBe(1);
    }

    [Fact]
    public void AddCrawldadClient_rejects_a_null_configure()
    {
        var services = new ServiceCollection();
        Should.Throw<ArgumentNullException>(() => services.AddCrawldadClient(null!));
    }

    [Fact]
    public void AddCrawldadClient_validates_a_missing_base_url()
    {
        var services = new ServiceCollection();
        Should.Throw<InvalidOperationException>(() => services.AddCrawldadClient(options => options.ApiKey = "k0123456789abcdef"));
    }

    [Fact]
    public void AddCrawldadClient_validates_a_missing_api_key()
    {
        var services = new ServiceCollection();
        Should.Throw<InvalidOperationException>(() => services.AddCrawldadClient(options => options.BaseUrl = new Uri("https://api.crawldad.test")));
    }

    [Fact]
    public void Options_validate_normalizes_a_base_url_without_a_trailing_slash()
    {
        var options = new CrawldadClientOptions { BaseUrl = new Uri("https://api.crawldad.test/v1"), ApiKey = "k0123456789abcdef" };

        options.Validate().AbsoluteUri.ShouldBe("https://api.crawldad.test/v1/");
    }

    [Fact]
    public void Options_validate_leaves_a_trailing_slash_untouched()
    {
        var options = new CrawldadClientOptions { BaseUrl = new Uri("https://api.crawldad.test/"), ApiKey = "k0123456789abcdef" };

        options.Validate().AbsoluteUri.ShouldBe("https://api.crawldad.test/");
    }

    [Fact]
    public void Constructor_applies_the_base_url_when_the_http_client_has_none()
    {
        using var http = new HttpClient();
        _ = new CrawldadClient(http, new CrawldadClientOptions { BaseUrl = new Uri("https://api.crawldad.test/v1"), ApiKey = "k0123456789abcdef" });

        http.BaseAddress!.AbsoluteUri.ShouldBe("https://api.crawldad.test/v1/"); // normalized with a trailing slash
    }

    [Fact]
    public void Constructor_leaves_an_existing_base_address_untouched()
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://existing.test/") };
        _ = new CrawldadClient(http, new CrawldadClientOptions { BaseUrl = new Uri("https://api.crawldad.test/"), ApiKey = "k0123456789abcdef" });

        http.BaseAddress.AbsoluteUri.ShouldBe("https://existing.test/");
    }

    [Fact]
    public void Constructor_tolerates_no_base_url_and_no_base_address()
    {
        using var http = new HttpClient();
        _ = new CrawldadClient(http, new CrawldadClientOptions { ApiKey = "k0123456789abcdef" });

        http.BaseAddress.ShouldBeNull();
    }

    [Fact]
    public void Constructor_rejects_null_arguments()
    {
        using var http = new HttpClient();
        Should.Throw<ArgumentNullException>(() => new CrawldadClient(null!, new CrawldadClientOptions()));
        Should.Throw<ArgumentNullException>(() => new CrawldadClient(http, null!));
    }
}
