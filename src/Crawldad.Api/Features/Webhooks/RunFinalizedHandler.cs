using Crawldad.Api.Features.Runs;
using Marten;
using Wolverine;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>Fans a run's terminal disposition out to the tenant's subscribed webhook endpoints. Handles the durable
/// <see cref="RunFinalized"/> signal (published after a run commits terminal), builds the delivery envelope once from the
/// committed run state, and cascades one <see cref="DeliverWebhook"/> per matching endpoint — off the run's execution path,
/// so a slow or failing webhook subsystem never touches run execution. A run with no subscribers, or whose stream was
/// erased before this ran, is a clean no-op. The session is tenant-scoped by the message envelope.</summary>
public static class RunFinalizedHandler
{
    /// <summary>Builds the envelope and cascades a delivery per subscribed endpoint (empty subscription = all events).</summary>
    public static async Task<OutgoingMessages> Handle(RunFinalized message, IDocumentSession session, IWebhookEndpointStore store, CancellationToken ct)
    {
        var envelope = await WebhookPayload.BuildAsync(session, message.RunId, ct);
        if (envelope is null)
        {
            return []; // no terminal event (still running, or the stream was erased) — nothing to deliver
        }

        var subscribed = (await store.ListAsync(session, ct))
            .Where(webhook => webhook.Events.Count == 0 || webhook.Events.Contains(envelope.Type, StringComparer.Ordinal))
            .ToList();
        if (subscribed.Count == 0)
        {
            return []; // this tenant has no endpoint subscribed to this event type
        }

        var body = WebhookJson.Serialize(envelope);
        var messages = new OutgoingMessages();
        foreach (var webhook in subscribed)
        {
            messages.Add(new DeliverWebhook(webhook.Name, envelope.Type, envelope.Id, body, 1, message.RunId));
        }

        return messages;
    }
}
