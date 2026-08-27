using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>Real-SSR rendering of the billing-result landing page (where the checkout / portal redirects and the
/// not-available fallback land): each outcome renders its own friendly message, and a checkout names the tier (or a
/// neutral placeholder when none is supplied). Authenticated, no API calls of its own.</summary>
[Collection(PortalCollection.Name)]
public class BillingResultTests(PortalFixture fixture)
{
    private static string NewEmail() => $"billing-{Guid.NewGuid():N}@example.com";

    [Theory]
    [InlineData("outcome=checkout&tier=team", "Checkout started", "team")]
    [InlineData("outcome=checkout", "Checkout started", "selected")]        // no tier → neutral placeholder
    [InlineData("outcome=portal", "Billing portal", "billing portal")]
    [InlineData("outcome=unavailable", "Billing not yet available", "nothing was changed")]
    [InlineData("outcome=whatever", "Billing", "head back to your account")] // unknown → default
    public async Task The_result_page_renders_the_outcome(string query, string title, string bodyFragment)
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var resp = await client.GetAsync(PortalHttp.Rel($"/app/account/billing-result?{query}"));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain(title);
        html.ShouldContain(bodyFragment, Case.Insensitive);
    }
}
