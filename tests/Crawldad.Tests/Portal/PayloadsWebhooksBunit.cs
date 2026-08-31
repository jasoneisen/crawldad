using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;

namespace Crawldad.Tests.Portal;

/// <summary>Test doubles for rendering the payloads/webhooks data pages under bUnit: a programmable
/// <see cref="IPortalTenantContext"/> that resolves to a <see cref="PortalTenant"/> whose <c>CrawldadClient</c> rides a
/// stub HTTP handler (canned API responses, keyed by path/method), or to the no-workspace (null) state — with or without
/// console access configured. Named for this feature so it never collides with a sibling page task's own doubles.</summary>
internal sealed class PayloadsWebhooksTenantContext : IPortalTenantContext
{
    private readonly PortalTenant? _tenant;

    private PayloadsWebhooksTenantContext(PortalTenant? tenant, bool configured)
    {
        _tenant = tenant;
        ConsoleConfigured = configured;
    }

    public bool ConsoleConfigured { get; }

    /// <summary>A context that resolves to a workspace whose client is backed by <paramref name="handler"/>.</summary>
    public static PayloadsWebhooksTenantContext LinkedTo(StubHttpMessageHandler handler, string tenantId = "meridian-title") =>
        new(new PortalTenant(tenantId, ClientTestHarness.ClientFor(handler)), configured: true);

    /// <summary>A context that resolves to the no-workspace state (console configured, but no active workspace).</summary>
    public static PayloadsWebhooksTenantContext NotLinked() => new(tenant: null, configured: true);

    /// <summary>A context where console access is not configured on the deployment (the honest unconfigured state).</summary>
    public static PayloadsWebhooksTenantContext NotConfigured() => new(tenant: null, configured: false);

    public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_tenant);

    public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) =>
        _tenant is not null ? Task.FromResult(_tenant)
        : throw new NotLinkedException("The current portal user has no active workspace.");
}
