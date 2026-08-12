using System.Text.Json.Serialization;
using Crawldad.Contracts.Runs;

namespace Crawldad.Contracts.Webhooks;

/// <summary>The JSON body POSTed to a registered webhook endpoint when a run reaches a terminal disposition. It carries
/// <b>references and metadata only</b> — never a run's <c>result</c>/<c>partial</c> content — so bodies stay small and
/// PII-free; a receiver fetches <c>GET /runs/{runId}</c> to read the result. Serialized with the shared wire conventions
/// (camelCase, string enums, null fields omitted). Delivery is <b>at-least-once</b>: dedupe on <see cref="RunId"/> +
/// <see cref="Status"/> (a run has exactly one terminal disposition). The <c>X-Crawldad-Signature</c> header authenticates it.</summary>
/// <param name="Id">A per-event id, echoed in the <c>X-Crawldad-Delivery</c> header and stable across retries of the same delivery.</param>
/// <param name="Type">The event type: <c>run.succeeded</c>, <c>run.failed</c>, or <c>run.cancelled</c> (echoed in <c>X-Crawldad-Event</c>).</param>
/// <param name="RunId">The run that reached terminal — poll <c>GET /runs/{runId}</c> for its result.</param>
/// <param name="PayloadId">The pinned managed payload id, when the run executed a managed payload (absent for an inline run).</param>
/// <param name="Revision">The pinned payload revision, when applicable (absent for an inline run).</param>
/// <param name="Status">The terminal disposition (<c>succeeded</c> / <c>failed</c> / <c>cancelled</c>).</param>
/// <param name="Failure">The typed failure, present only for a <c>run.failed</c> event.</param>
/// <param name="Stats">The run counters (duration, steps, requests, downloads, selector misses).</param>
/// <param name="FinishedAt">When the run reached terminal.</param>
public sealed record WebhookEventEnvelope(
    string Id,
    string Type,
    Guid RunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PayloadId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Revision,
    RunStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunFailureDetail? Failure,
    RunStats Stats,
    DateTimeOffset FinishedAt);
