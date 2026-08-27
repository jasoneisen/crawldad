namespace Crawldad.Api.Infrastructure.Storage;

/// <summary>A non-blob retention sweep the <see cref="RetentionJanitor"/> drives each pass, beside the durable blob
/// stores it ages out. A durable read model with its own PII/TTL concern that is <b>not</b> a blob — e.g. an async run's
/// stored result in the executor-owned <c>RunProgress</c> Marten document, which <see cref="IRetentionStore"/> never
/// reached — expires its aged rows here, on the same schedule and under the same
/// <see cref="RetentionOptions.Enabled"/>/<see cref="RetentionOptions.SweepInterval"/> policy, so the host keeps <b>one</b>
/// retention cadence rather than one background service per store. The parallel of the <see cref="IRetentionStore"/> seam
/// (which the janitor drives for blobs): implementations MUST be tenant-correct and bound the work per pass (the janitor
/// invokes this once per interval, not in a loop).</summary>
public interface IRetentionSweep
{
    /// <summary>Expires whatever rows this sweep owns that have aged past their TTL as of <paramref name="now"/>, and
    /// returns how many it expired this pass (folded into the janitor's summary count). Pure with respect to
    /// <paramref name="now"/> so a test drives expiry deterministically; a per-item failure is the sweep's own concern,
    /// but a whole-sweep throw is caught by the janitor (logged, the rest of the pass continues).</summary>
    Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct);
}
