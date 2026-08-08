using System.Security.Claims;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The per-request tenant accessor (CD-1): reads the tenant + actor from the authenticated principal the API-key handler
/// issued. Every route that resolves it requires authentication, so the claims are present; a missing principal is a loud
/// failure rather than a silent default-tenant fallback.
/// </summary>
public class TenantContextTests
{
    private sealed class StubHttpContextAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private static TenantContext ContextWith(params Claim[] claims)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new TenantContext(new StubHttpContextAccessor(http));
    }

    [Fact]
    public void Reads_the_tenant_and_actor_from_the_principal()
    {
        var tenant = ContextWith(new Claim(CrawldadClaims.TenantId, "alpha"), new Claim(CrawldadClaims.Actor, "alpha@x"));

        tenant.TenantId.ShouldBe("alpha");
        tenant.Actor.ShouldBe("alpha@x");
    }

    [Fact]
    public void Throws_when_there_is_no_authenticated_request()
    {
        var tenant = new TenantContext(new StubHttpContextAccessor(null));

        Should.Throw<InvalidOperationException>(() => tenant.TenantId);
    }

    [Fact]
    public void Throws_when_the_actor_claim_is_absent()
    {
        var tenant = ContextWith(new Claim(CrawldadClaims.TenantId, "alpha")); // tenant present, actor missing

        Should.Throw<InvalidOperationException>(() => tenant.Actor);
    }
}
