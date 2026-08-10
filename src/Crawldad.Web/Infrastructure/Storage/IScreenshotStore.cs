namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>The failure-screenshot blob sink: the interpreter streams a captured PNG through to a deletable blob
/// store, which returns a ref the <c>StepFailed</c> event stores — never the image itself, since a screenshot can
/// show PII and must stay erasable. Content-addressed and tenant-scoped, so one tenant's captures stay isolated.</summary>
public interface IScreenshotStore
{
    /// <summary>Stores a captured screenshot under the tenant's partition and returns its content-addressed blob ref
    /// the <c>StepFailed</c> event records.</summary>
    Task<string> SaveAsync(string tenant, byte[] png, CancellationToken ct);

    /// <summary>Opens a stored screenshot from the tenant's own partition for read-back (the retrieval endpoint's seam),
    /// or null when no blob exists for the ref — an unknown ref, or one the retention janitor has deleted. The caller
    /// authorizes the ref against the run's trace first; this read performs no run-association check.</summary>
    Task<Stream?> OpenReadAsync(string tenant, string reference, CancellationToken ct);
}
