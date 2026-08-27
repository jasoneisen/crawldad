using System.Text.Json;
using Crawldad.Api.Features.Runs;
using Crawldad.Contracts;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Webhooks;
using Marten;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>Builds the terminal-run delivery envelope by folding a run's event stream — the single source that is correct
/// for every finalisation path (executor, queue cancel, queue-wait timeout) and carries the exact scrubbed terminal facts.
/// The opening event (<c>RunStarted</c>/<c>RunQueued</c>) supplies the pinned payload identity; the terminal event
/// (<c>RunSucceeded</c>/<c>RunFailed</c>/<c>RunCancelled</c>) supplies the status, stats, failure, and finish time. Returns
/// <see langword="null"/> when the run has no terminal event yet or its stream was erased (<c>DELETE /runs/{id}</c>) before
/// the delivery ran — a no-op, not an error.</summary>
internal static class WebhookPayload
{
    /// <summary>Folds the tenant-scoped <paramref name="runId"/> stream into a delivery envelope, or null when it has no terminal event.</summary>
    public static async Task<WebhookEventEnvelope?> BuildAsync(IQuerySession session, Guid runId, CancellationToken ct)
    {
        var events = await session.Events.FetchStreamAsync(runId, token: ct);

        Guid? payloadId = null;
        int? revision = null;
        WebhookEventEnvelope? envelope = null;
        foreach (var stored in events)
        {
            switch (stored.Data)
            {
                case RunStarted started:
                    (payloadId, revision) = (started.PayloadId, started.PayloadRevision);
                    break;
                case RunQueued queued:
                    (payloadId, revision) = (queued.PayloadId, queued.PayloadRevision);
                    break;
                case RunSucceeded succeeded:
                    envelope = new WebhookEventEnvelope(NewId(), WebhookEventTypes.RunSucceeded, runId, payloadId, revision, RunStatus.Succeeded, null, succeeded.Stats, succeeded.FinishedAt);
                    break;
                case RunFailed failed:
                    envelope = new WebhookEventEnvelope(NewId(), WebhookEventTypes.RunFailed, runId, payloadId, revision, RunStatus.Failed, failed.Failure, failed.Stats, failed.FinishedAt);
                    break;
                case RunCancelled cancelled:
                    envelope = new WebhookEventEnvelope(NewId(), WebhookEventTypes.RunCancelled, runId, payloadId, revision, RunStatus.Cancelled, null, cancelled.Stats, cancelled.FinishedAt);
                    break;
                default:
                    break; // non-terminal trace events carry nothing the envelope needs
            }
        }

        return envelope;
    }

    private static string NewId() => Guid.NewGuid().ToString();
}

/// <summary>The single JSON convention for a webhook body: the shared wire options (camelCase, string enums via
/// <see cref="ContractsJson"/>), so a delivery serializes byte-for-byte like the rest of the API's contracts.</summary>
internal static class WebhookJson
{
    private static readonly JsonSerializerOptions _options = Create();

    /// <summary>Serializes a delivery envelope to its canonical wire JSON.</summary>
    public static string Serialize(WebhookEventEnvelope envelope) => JsonSerializer.Serialize(envelope, _options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ContractsJson.Configure(options);
        return options;
    }
}
