namespace Crawldad.Api.Features.Webhooks;

/// <summary>One durable delivery of a webhook event to a single endpoint. Carries the already-serialized, already-scrubbed
/// body (byte-identical across retries, so the signature is stable per attempt) plus the endpoint name and a 1-based
/// <see cref="Attempt"/>. Published (fanned out, one per subscribed endpoint) by <see cref="RunFinalizedHandler"/> and
/// handled by <see cref="DeliverWebhookHandler"/>, which reschedules the next attempt on failure. A durable local-queue
/// message, tenant-scoped by the envelope, so a delivery survives a restart.</summary>
/// <param name="EndpointName">The tenant's registered endpoint name (the Marten document id) to deliver to.</param>
/// <param name="EventType">The event type (<c>run.succeeded</c> / <c>run.failed</c> / <c>run.cancelled</c>), for the <c>X-Crawldad-Event</c> header.</param>
/// <param name="EventId">The event id, for the <c>X-Crawldad-Delivery</c> header (stable across this delivery's retries).</param>
/// <param name="Body">The exact JSON body to POST and to sign.</param>
/// <param name="Attempt">The 1-based attempt number (1 on first send, incremented on each retry).</param>
public sealed record DeliverWebhook(string EndpointName, string EventType, string EventId, string Body, int Attempt);
