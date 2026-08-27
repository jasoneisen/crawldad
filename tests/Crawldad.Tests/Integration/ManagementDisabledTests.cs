using Alba;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Integration;

/// <summary>With no <c>Management:ApiKey</c> configured (the default host, e.g. <see cref="AppFixture"/>), the management
/// surface is disabled: its routes are never mapped, so every <c>/management/…</c> request is a plain <c>404</c> — the
/// documented "disabled" behaviour, and the reason the management endpoints never leak onto a host that didn't opt in.</summary>
[Collection(IntegrationCollection.Name)]
public class ManagementDisabledTests(AppFixture fixture)
{
    [Fact]
    public async Task Management_routes_are_unmapped_and_404_when_no_key_is_configured()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { id = "x", displayName = "X" }).ToUrl("/management/tenants");
            x.StatusCodeShouldBe(StatusCodes.Status404NotFound);
        });

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/management/tenants/anything");
            x.StatusCodeShouldBe(StatusCodes.Status404NotFound);
        });
    }
}
