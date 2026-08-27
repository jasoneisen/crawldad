using Marten;

namespace Crawldad.Api.Features.Billing;

/// <summary>One processed inbound billing event, kept so a redelivery of the same provider event is a no-op (anti-replay).
/// Stored <b>single-tenanted</b> alongside the tenant registry: the webhook is unauthenticated and not tenant-scoped, so
/// like the registry documents it is written on the default partition. The document id is the provider event id.</summary>
public sealed class ProcessedBillingEvent
{
    /// <summary>The provider event id — the document id and the dedup key.</summary>
    public string Id { get; set; } = "";

    /// <summary>When the event was first processed (UTC).</summary>
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>The anti-replay seam over <see cref="ProcessedBillingEvent"/>. Split out from Marten so the webhook handler's
/// dedup branch is unit-testable against a fake and the Marten wiring is exercised end-to-end.</summary>
public interface IProcessedBillingEventStore
{
    /// <summary>Records <paramref name="eventId"/> as processed. Returns true when it was newly recorded (the caller should
    /// act on the event), false when it was already present (a replay — the caller no-ops). Sequential redeliveries — the
    /// case this guards — dedup reliably; a pair of exactly-concurrent first deliveries is a rare, benign race the caller's
    /// idempotent plan write already tolerates.</summary>
    Task<bool> TryRecordAsync(string eventId, DateTimeOffset now, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="IProcessedBillingEventStore"/>. Single-tenanted (the registry partition), a
/// session opened per call from the shared store — the same shape the registry store uses.</summary>
internal sealed class MartenProcessedBillingEventStore(IDocumentStore store) : IProcessedBillingEventStore
{
    public async Task<bool> TryRecordAsync(string eventId, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        if (await session.LoadAsync<ProcessedBillingEvent>(eventId, ct) is not null)
        {
            return false; // already processed — a replay
        }

        session.Store(new ProcessedBillingEvent { Id = eventId, ProcessedAt = now });
        await session.SaveChangesAsync(ct);
        return true;
    }
}
