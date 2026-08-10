namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>The failure-screenshot blob sink: the interpreter streams a captured PNG through to a deletable blob
/// store, which returns a ref the <c>StepFailed</c> event stores — never the image itself, since a screenshot can
/// show PII and must stay erasable. Content-addressed and tenant-scoped, so one tenant's captures stay isolated.</summary>
public interface IScreenshotStore
{
    /// <summary>Stores a captured screenshot under the tenant's partition and returns its content-addressed blob ref
    /// the <c>StepFailed</c> event records.</summary>
    Task<string> SaveAsync(string tenant, byte[] png, CancellationToken ct);
}
