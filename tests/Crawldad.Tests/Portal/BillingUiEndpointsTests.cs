using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Billing;
using Crawldad.Portal.Billing;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crawldad.Tests.Portal;

/// <summary>The billing card's form handlers: resolve the tenant, ask the SDK for a hosted-page URL, and redirect to it —
/// or, when not linked or the API errors, redirect safely to the account / "not yet available" pages rather than 500.</summary>
public class BillingUiEndpointsTests
{
    private static readonly IServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();

    [Fact]
    public async Task Checkout_redirects_to_the_minted_url()
    {
        var result = await BillingUiEndpoints.CheckoutAsync(Http(), Linked(SessionReturning("/app/account/billing-result?outcome=checkout&tier=team")), "team");

        var (status, location) = await RunAsync(result);
        status.ShouldBe(StatusCodes.Status302Found);
        location.ShouldBe("/app/account/billing-result?outcome=checkout&tier=team");
    }

    [Fact]
    public async Task Checkout_redirects_to_the_account_when_not_linked()
    {
        var result = await BillingUiEndpoints.CheckoutAsync(Http(), Unlinked(), "team");

        (await RunAsync(result)).Location.ShouldBe(BillingUiEndpoints.AccountPath);
    }

    [Fact]
    public async Task Checkout_redirects_to_the_account_when_no_tier_is_posted()
    {
        var result = await BillingUiEndpoints.CheckoutAsync(Http(), Linked(SessionReturning("unused")), tier: "");

        (await RunAsync(result)).Location.ShouldBe(BillingUiEndpoints.AccountPath);
    }

    [Fact]
    public async Task Checkout_redirects_to_the_unavailable_page_when_the_api_errors()
    {
        var result = await BillingUiEndpoints.CheckoutAsync(Http(), Linked(Failing()), "team");

        (await RunAsync(result)).Location.ShouldBe(BillingUiEndpoints.UnavailableResult);
    }

    [Fact]
    public async Task Portal_redirects_to_the_minted_url()
    {
        var result = await BillingUiEndpoints.PortalAsync(Http(), Linked(SessionReturning("/app/account/billing-result?outcome=portal")));

        var (status, location) = await RunAsync(result);
        status.ShouldBe(StatusCodes.Status302Found);
        location.ShouldBe("/app/account/billing-result?outcome=portal");
    }

    [Fact]
    public async Task Portal_redirects_to_the_account_when_not_linked()
    {
        var result = await BillingUiEndpoints.PortalAsync(Http(), Unlinked());

        (await RunAsync(result)).Location.ShouldBe(BillingUiEndpoints.AccountPath);
    }

    [Fact]
    public async Task Portal_redirects_to_the_unavailable_page_when_the_api_errors()
    {
        var result = await BillingUiEndpoints.PortalAsync(Http(), Linked(Failing()));

        (await RunAsync(result)).Location.ShouldBe(BillingUiEndpoints.UnavailableResult);
    }

    private static DefaultHttpContext Http() => new() { RequestServices = _services };

    private static FakeContext Unlinked() => new(null);

    private static FakeContext Linked(StubHttpMessageHandler handler) =>
        new(new PortalTenant("tenant-alpha", new CrawldadClient(
            new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey })));

    private static StubHttpMessageHandler SessionReturning(string url) =>
        new(_ => ClientTestHarness.Json(new BillingSessionResponse(url)));

    private static StubHttpMessageHandler Failing() =>
        new(_ => ClientTestHarness.JsonRaw(HttpStatusCode.ServiceUnavailable, """{"title":"billing_not_configured","status":503}"""));

    private static async Task<(int Status, string? Location)> RunAsync(IResult result)
    {
        var http = Http();
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, http.Response.Headers.Location);
    }

    private sealed class FakeContext(PortalTenant? tenant) : IPortalTenantContext
    {
        public bool ConsoleConfigured => true;

        public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant);

        public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant!);
    }
}
