using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Portal.Tenancy;

/// <summary>The console-read → stored-key fallback (issue #119 PR4). Wraps the portal's API handler on a console-mode
/// client: a request first goes out with the console credential (bearer token + selectors); if the API rejects it with a
/// <c>401</c>/<c>403</c> — a write endpoint the console can't reach yet (PR5), or a read whose membership does not exist —
/// the request is retried once with the tenant's stored key <b>alone</b> (never both credentials in one request, which the
/// API rejects). This is the dual-run safety net: console reads with a membership succeed on the first try; everything else
/// still works via the key until the console path fully lands.</summary>
internal sealed class ConsoleReadFallbackHandler : DelegatingHandler
{
    private readonly ApiKeyCredential _keyCredential;

    /// <summary>Wraps <paramref name="innerHandler"/> with a fallback to <paramref name="apiKey"/>.</summary>
    /// <param name="innerHandler">The pooled API message handler (owned by the factory; never disposed here).</param>
    /// <param name="apiKey">The tenant's stored key to fall back to.</param>
    public ConsoleReadFallbackHandler(HttpMessageHandler innerHandler, string apiKey)
    {
        InnerHandler = innerHandler;
        _keyCredential = new ApiKeyCredential(apiKey);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Buffer any request body up front so the fallback can re-send it — an HttpRequestMessage is single-use.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return response; // the console token was accepted (or a genuine error) — nothing to fall back to
        }

        // Console was rejected: retry once with the stored tenant key alone.
        response.Dispose();
        using var retry = CloneWithKey(request, body);
        await _keyCredential.ApplyAsync(retry, cancellationToken).ConfigureAwait(false); // Authorization: Bearer {tenant key}
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    // Rebuilds the request carrying everything except the console credential, so re-applying the key credential leaves
    // exactly the key on it (no bearer console token, no selectors — never both credentials).
    private static HttpRequestMessage CloneWithKey(HttpRequestMessage original, byte[]? body)
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

        if (body is not null)
        {
            var content = new ByteArrayContent(body);
            foreach (var contentHeader in original.Content!.Headers)
            {
                content.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);
            }

            retry.Content = content;
        }

        return retry;
    }
}
