using System.Collections.Concurrent;
using Crawldad.Web.Features.Webhooks;

namespace Crawldad.Tests.Support;

/// <summary>One recorded delivery attempt the <see cref="RecordingWebhookSender"/> saw.</summary>
internal sealed record WebhookCall(string Url, string Body, IReadOnlyDictionary<string, string> Headers, TimeSpan Timeout);

/// <summary>A test <see cref="IWebhookSender"/> that records every attempt and returns a programmable result, so the
/// webhook suite exercises delivery/retry/give-up with no real network. Thread-safe: the durable pipeline calls it from
/// a background worker. Sequential test classes re-arm it per test via <see cref="Behave"/>.</summary>
internal sealed class RecordingWebhookSender : IWebhookSender
{
    private readonly ConcurrentQueue<WebhookCall> _calls = new();
    private volatile Func<int, WebhookSendResult> _behavior = static _ => new WebhookSendResult(true, 200);

    /// <summary>Every recorded attempt, in order.</summary>
    public IReadOnlyList<WebhookCall> Calls => [.. _calls];

    /// <summary>The number of attempts seen so far.</summary>
    public int CallCount => _calls.Count;

    /// <summary>The most recent recorded attempt, or null if none.</summary>
    public WebhookCall? Last => _calls.LastOrDefault();

    /// <summary>Clears the recorded calls and sets the per-attempt behaviour (its argument is the 1-based global attempt count).</summary>
    public void Behave(Func<int, WebhookSendResult> behavior)
    {
        _calls.Clear();
        _behavior = behavior;
    }

    /// <summary>Always accept (2xx).</summary>
    public void AlwaysDeliver() => Behave(static _ => new WebhookSendResult(true, 200));

    /// <summary>Always fail with the given status (null = a transport failure with no status).</summary>
    public void AlwaysFail(int? status = 500) => Behave(_ => new WebhookSendResult(false, status));

    /// <summary>Fail the first <paramref name="failures"/> attempts (503), then accept.</summary>
    public void FailThenDeliver(int failures) =>
        Behave(attempt => attempt <= failures ? new WebhookSendResult(false, 503) : new WebhookSendResult(true, 200));

    /// <inheritdoc />
    public Task<WebhookSendResult> SendAsync(string url, string body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken ct)
    {
        _calls.Enqueue(new WebhookCall(url, body, new Dictionary<string, string>(headers, StringComparer.Ordinal), timeout));
        return Task.FromResult(_behavior(_calls.Count));
    }
}
