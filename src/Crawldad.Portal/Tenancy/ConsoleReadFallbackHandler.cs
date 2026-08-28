using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Portal.Tenancy;

/// <summary>The console-read → stored-key fallback (issue #119 PR4/PR5). Wraps the portal's API handler on a console-mode
/// client that still has a transition stored key: a <b>read</b> (GET) first goes out with the console credential (bearer
/// token + selectors); if the API rejects it with a <c>401</c>/<c>403</c> — a read whose membership does not exist yet — the
/// request is retried once with the tenant's stored key <b>alone</b> (never both credentials in one request, which the API
/// rejects). <b>Writes are console-only</b> (PR5): a non-GET is never retried with the key, so a console write that is
/// rejected surfaces its rejection rather than silently re-running on the stored key. This is the narrow transition safety
/// net — console reads converge on the first try once a membership exists, and it disappears entirely once the stored key is
/// retired (the factory then wires the handler with no fallback at all).</summary>
internal sealed class ConsoleReadFallbackHandler : DelegatingHandler
{
    private readonly ApiKeyCredential _keyCredential;

    /// <summary>Wraps <paramref name="innerHandler"/> with a read-only fallback to <paramref name="apiKey"/>.</summary>
    /// <param name="innerHandler">The pooled API message handler (owned by the factory; never disposed here).</param>
    /// <param name="apiKey">The tenant's stored key to fall back to on a rejected read.</param>
    public ConsoleReadFallbackHandler(HttpMessageHandler innerHandler, string apiKey)
    {
        InnerHandler = innerHandler;
        _keyCredential = new ApiKeyCredential(apiKey);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Writes are console-only: never retry a non-GET with the stored key. A rejected console write surfaces as-is.
        if (request.Method != HttpMethod.Get)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return response; // the console token was accepted (or a genuine error) — nothing to fall back to
        }

        // Console read was rejected: retry once with the stored tenant key alone. A GET carries no body to re-buffer.
        response.Dispose();
        using var retry = CloneWithKey(request);
        await _keyCredential.ApplyAsync(retry, cancellationToken).ConfigureAwait(false); // Authorization: Bearer {tenant key}
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    // Rebuilds the GET request carrying everything except the console credential, so re-applying the key credential leaves
    // exactly the key on it (no bearer console token, no selectors — never both credentials). A GET has no body to re-send.
    private static HttpRequestMessage CloneWithKey(HttpRequestMessage original)
    {
        var retry = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, ConsoleAuthHeaders.ConsoleUser, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, ConsoleAuthHeaders.Workspace, StringComparison.OrdinalIgnoreCase))
            {
                continue; // drop the console credential; the key credential replaces it
            }

            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return retry;
    }
}
