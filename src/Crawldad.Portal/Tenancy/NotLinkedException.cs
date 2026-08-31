namespace Crawldad.Portal.Tenancy;

/// <summary>Thrown by <see cref="IPortalTenantContext.RequireAsync"/> when the current request has no workspace to act
/// as — it is unauthenticated, console access is unconfigured, or the signed-in account has no active workspace yet
/// (<see cref="PortalWorkspaceSelection"/>). The data pages catch this to render their empty state; the tenant context
/// never hands back a <c>CrawldadClient</c> with no credential.</summary>
public sealed class NotLinkedException : Exception
{
    /// <summary>Initializes the exception with a human-readable reason.</summary>
    /// <param name="message">Why no tenant could be resolved for the current request.</param>
    public NotLinkedException(string message)
        : base(message)
    {
    }
}
