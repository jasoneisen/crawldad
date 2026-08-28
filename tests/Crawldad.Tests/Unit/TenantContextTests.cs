using System.Security.Claims;
using Crawldad.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Unit;

/// <summary>The per-request tenant accessor: reads the tenant + actor from the authenticated principal the API-key handler
/// issued. Every route that resolves it requires authentication, so the claims are present; a missing principal is a loud
/// failure rather than a silent default-tenant fallback.</summary>
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
        var tenant = ContextWith(new Claim(CrawldadClaims.TenantId, "alpha"));

        Should.Throw<InvalidOperationException>(() => tenant.Actor);
    }

    [Fact]
    public void Throws_on_two_distinct_tenant_claims_rather_than_picking_the_first()
    {
        // The finding #4 hazard: two schemes merged into one principal, each stamping a different tenant. Fail closed so an
        // ambiguous request can never resolve to a tenant it only half-proved (the console path forbids the merge upstream).
        var tenant = ContextWith(new Claim(CrawldadClaims.TenantId, "alpha"), new Claim(CrawldadClaims.TenantId, "beta"));

        Should.Throw<InvalidOperationException>(() => tenant.TenantId);
    }

    [Fact]
    public void Two_identical_tenant_claims_resolve_to_the_one_value()
    {
        // Distinct values, not distinct claim objects: the same tenant stamped twice is unambiguous, not a conflict.
        var tenant = ContextWith(new Claim(CrawldadClaims.TenantId, "alpha"), new Claim(CrawldadClaims.TenantId, "alpha"));

        tenant.TenantId.ShouldBe("alpha");
    }
}
