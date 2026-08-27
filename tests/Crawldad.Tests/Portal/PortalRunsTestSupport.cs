using Crawldad.Client;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;

namespace Crawldad.Tests.Portal;

/// <summary>An <see cref="IPortalTenantContext"/> that hands back a fixed resolution — a linked <see cref="PortalTenant"/>
/// or the not-linked (null) state — so the runs pages and the screenshot proxy can be rendered/invoked in isolation over
/// a stubbed API, with no real HttpContext, link store, or Data-Protection ring.</summary>
internal sealed class FakePortalTenantContext(PortalTenant? tenant) : IPortalTenantContext
{
    public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tenant);

    public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) =>
        tenant is not null
            ? Task.FromResult(tenant)
            : throw new NotLinkedException("The fake tenant context has no linked tenant.");
}

/// <summary>Builds a <see cref="PortalTenant"/> whose <see cref="CrawldadClient"/> rides a stub transport, so a page's
/// data reads resolve to scripted API responses.</summary>
internal static class PortalRunsTestSupport
{
    private static readonly Uri _apiBase = new("https://api.crawldad.test/");

    /// <summary>A tenant whose client answers over <paramref name="handler"/>.</summary>
    public static PortalTenant TenantOver(StubHttpMessageHandler handler, string tenantId = "tenant-test")
    {
        var http = new HttpClient(handler) { BaseAddress = _apiBase };
        var client = new CrawldadClient(http, new CrawldadClientOptions { BaseUrl = _apiBase, ApiKey = "test-key-abcdef" });
        return new PortalTenant(tenantId, client);
    }
}
