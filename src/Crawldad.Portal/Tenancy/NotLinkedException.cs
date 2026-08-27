namespace Crawldad.Portal.Tenancy;

/// <summary>Thrown by <see cref="IPortalTenantContext.RequireAsync"/> when the current request has no tenant to act
/// as — either it is unauthenticated, or the signed-in account has no <see cref="PortalTenantLink"/> yet. The data
/// pages catch this to render their "link your tenant" empty state; the tenant context never hands back a
/// <c>CrawldadClient</c> with an empty or absent API key.</summary>
public sealed class NotLinkedException : Exception
{
    /// <summary>Initializes the exception with a human-readable reason.</summary>
    /// <param name="message">Why no tenant could be resolved for the current request.</param>
    public NotLinkedException(string message)
        : base(message)
    {
    }
}
