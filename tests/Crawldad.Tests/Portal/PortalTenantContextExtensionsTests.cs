using System.Net;
using System.Security.Cryptography;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for the shared page-resolve helper
/// <see cref="PortalTenantContextExtensions.TryResolveForPageAsync"/>: it passes a linked tenant straight through,
/// passes the not-linked null through, and — the reason it exists — folds an undecryptable stored key (a rotated
/// Data-Protection ring surfacing as <see cref="CryptographicException"/> from inside Unprotect) into that same null, so
/// the data pages render their not-linked empty state instead of a 500.</summary>
public class PortalTenantContextExtensionsTests
{
    // The helper never calls the API (it only resolves the tenant), so the handler's responses are never read.
    private static StubHttpMessageHandler AnyHandler() => new(_ => ClientTestHarness.Empty(HttpStatusCode.OK));

    [Fact]
    public async Task Passes_a_resolved_tenant_straight_through()
    {
        var context = PayloadsWebhooksTenantContext.LinkedTo(AnyHandler(), tenantId: "tenant-7");

        var tenant = await context.TryResolveForPageAsync();

        tenant.ShouldNotBeNull();
        tenant.TenantId.ShouldBe("tenant-7");
    }

    [Fact]
    public async Task Passes_the_not_linked_null_through() =>
        (await PayloadsWebhooksTenantContext.NotLinked().TryResolveForPageAsync()).ShouldBeNull();

    [Fact]
    public async Task An_undecryptable_key_resolves_to_the_not_linked_null()
    {
        // A rotated/lost ring makes TryResolveAsync throw CryptographicException; the helper swallows it to null (a
        // not-linked-shaped state that points the user at Account to re-link) rather than surfacing a 500.
        var context = PayloadsWebhooksTenantContext.Throwing(new CryptographicException("ring rotated"));

        (await context.TryResolveForPageAsync()).ShouldBeNull();
    }
}
