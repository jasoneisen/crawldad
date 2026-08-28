using Crawldad.Portal.Auth;
using Crawldad.Portal.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>The Marten-backed active-workspace selection store (issue #119 PR6) against the real portal store: absent by
/// default, round-trips by normalized email, and a re-set replaces the previous selection (one active workspace per account).</summary>
[Collection(PortalCollection.Name)]
public class PortalWorkspaceSelectionStoreTests(PortalFixture fixture)
{
    private static string NewEmail() => $"sel-{Guid.NewGuid():N}@example.com";

    private IPortalWorkspaceSelectionStore Store => fixture.App.Services.GetRequiredService<IPortalWorkspaceSelectionStore>();

    [Fact]
    public async Task Get_returns_null_when_nothing_is_selected()
    {
        (await Store.GetAsync(NewEmail())).ShouldBeNull();
    }

    [Fact]
    public async Task Set_then_get_round_trips_by_normalized_email()
    {
        var email = NewEmail();
        await Store.SetAsync(email, "tenant-active");

        // Stored lower-invariant → a mixed-case lookup still finds it (the PortalUser identity rule).
        var got = await Store.GetAsync(email.ToUpperInvariant());

        got.ShouldNotBeNull();
        got.TenantId.ShouldBe("tenant-active");
        got.Email.ShouldBe(PortalAuthService.NormalizeEmail(email));
    }

    [Fact]
    public async Task Set_replaces_the_previous_selection()
    {
        var email = NewEmail();
        await Store.SetAsync(email, "tenant-1");
        await Store.SetAsync(email, "tenant-2");

        (await Store.GetAsync(email))!.TenantId.ShouldBe("tenant-2"); // one active workspace per account
    }
}
