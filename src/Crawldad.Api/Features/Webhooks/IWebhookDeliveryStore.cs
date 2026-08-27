using Crawldad.Contracts.Webhooks;
using Marten;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>The tenant-scoped delivery-history store behind the webhook delivery log. The write path
/// (<see cref="RecordAsync"/>) takes the delivery handler's already-tenant-scoped session and appends a
/// <see cref="WebhookDelivery"/>, pruning that endpoint's history to the latest N. The read paths take the request's
/// already-tenant-scoped session — the recent log for one endpoint, and the latest-per-endpoint map that enriches the
/// webhook listing. Records carry no secret and never the signed body.</summary>
public interface IWebhookDeliveryStore
{
    /// <summary>Appends <paramref name="record"/> on the delivery handler's <paramref name="session"/> (not committed
    /// here — the Wolverine handler's transaction commits it), then prunes the endpoint's history to the newest
    /// <paramref name="maxPerEndpoint"/> so the log never grows without bound. A soft cap: concurrent deliveries to one
    /// endpoint may transiently leave a few extra rows, reconciled on the next record.</summary>
    Task RecordAsync(IDocumentSession session, WebhookDelivery record, int maxPerEndpoint, CancellationToken ct);

    /// <summary>The endpoint's recent delivery attempts on the request's <paramref name="session"/>, newest first, capped
    /// at <paramref name="limit"/> — each attempt (including a retry of the same event) a distinct row.</summary>
    Task<IReadOnlyList<WebhookDeliveryItem>> RecentAsync(IQuerySession session, string endpointName, int limit, CancellationToken ct);

    /// <summary>The latest delivery per endpoint for the tenant, keyed by endpoint name — the "last delivery" column the
    /// webhook listing attaches to each row. Reads the request's <paramref name="session"/>.</summary>
    Task<IReadOnlyDictionary<string, WebhookDeliverySummary>> LatestPerEndpointAsync(IQuerySession session, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="IWebhookDeliveryStore"/>. Every query rides the caller's tenant-scoped session,
/// so isolation holds by construction; pruning bounds the per-endpoint history so the log is a rolling window, not an
/// audit ledger.</summary>
internal sealed class MartenWebhookDeliveryStore : IWebhookDeliveryStore
{
    public async Task RecordAsync(IDocumentSession session, WebhookDelivery record, int maxPerEndpoint, CancellationToken ct)
    {
        session.Store(record);

        // Keep the newest maxPerEndpoint for this endpoint. The just-stored record is not yet committed, so it is invisible
        // to this query and is the newest by construction — keep (maxPerEndpoint - 1) existing rows plus it, delete the
        // rest. Ordered by (At desc, Id desc) so the cut is deterministic even when a frozen test clock ties every At.
        var existing = await session.Query<WebhookDelivery>()
            .Where(delivery => delivery.EndpointName == record.EndpointName)
            .OrderByDescending(delivery => delivery.At).ThenByDescending(delivery => delivery.Id)
            .Select(delivery => delivery.Id)
            .ToListAsync(ct);

        foreach (var id in existing.Skip(maxPerEndpoint - 1))
        {
            session.Delete<WebhookDelivery>(id);
        }
    }

    public async Task<IReadOnlyList<WebhookDeliveryItem>> RecentAsync(IQuerySession session, string endpointName, int limit, CancellationToken ct)
    {
        var rows = await session.Query<WebhookDelivery>()
            .Where(delivery => delivery.EndpointName == endpointName)
            .OrderByDescending(delivery => delivery.At).ThenByDescending(delivery => delivery.Id)
            .Take(limit)
            .ToListAsync(ct);
        return [.. rows.Select(ToItem)];
    }

    public async Task<IReadOnlyDictionary<string, WebhookDeliverySummary>> LatestPerEndpointAsync(IQuerySession session, CancellationToken ct)
    {
        // Bounded by (endpoints × the per-endpoint cap), so materialising the tenant's whole (small) delivery window and
        // taking the newest per endpoint in memory is cheaper than one correlated top-1-per-group query per endpoint.
        var all = await session.Query<WebhookDelivery>()
            .OrderByDescending(delivery => delivery.At).ThenByDescending(delivery => delivery.Id)
            .ToListAsync(ct);
        return all
            .GroupBy(delivery => delivery.EndpointName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => ToSummary(group.First()), StringComparer.Ordinal);
    }

    private static WebhookDeliveryItem ToItem(WebhookDelivery d) =>
        new(d.RunId, d.EventType, d.Attempt, d.Delivered, d.StatusCode, d.LatencyMs, d.At);

    private static WebhookDeliverySummary ToSummary(WebhookDelivery d) =>
        new(d.RunId, d.EventType, d.Attempt, d.Delivered, d.StatusCode, d.LatencyMs, d.At);
}
