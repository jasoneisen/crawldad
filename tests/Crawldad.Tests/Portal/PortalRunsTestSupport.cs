using Crawldad.Client;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;

namespace Crawldad.Tests.Portal;

/// <summary>An <see cref="IPortalTenantContext"/> that hands back a fixed resolution — a resolved <see cref="PortalTenant"/>
/// or the no-workspace (null) state — so the runs pages and the screenshot proxy can be rendered/invoked in isolation over
/// a stubbed API, with no real HttpContext. <paramref name="configured"/> models whether console access is configured on the
/// deployment (default true — dev/prod both run console-mode); pass false to render the honest "console access not configured"
/// empty state.</summary>
internal sealed class FakePortalTenantContext(PortalTenant? tenant, bool configured = true) : IPortalTenantContext
{
    public bool ConsoleConfigured => configured;

    public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tenant);

    public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) =>
        tenant is not null
            ? Task.FromResult(tenant)
            : throw new NotLinkedException("The fake tenant context has no active workspace.");
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
