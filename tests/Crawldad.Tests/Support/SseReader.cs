using System.Globalization;
using Alba;
using Microsoft.AspNetCore.TestHost;

namespace Crawldad.Tests.Support;

/// <summary>Reads a run's SSE stream (<c>GET /runs/{id}/events</c>) over the raw TestServer client — Alba's scenario API
/// is request/response only. Collects frames until the server closes the stream (the run reaches a terminal event);
/// a timeout guards against a run that never finishes.</summary>
internal static class SseReader
{
    /// <summary>One parsed SSE frame.</summary>
    /// <param name="Id">The frame id (the event's stream version).</param>
    public sealed record Frame(long Id, string Event, string Data);

    /// <summary>Reads all frames from a run's SSE stream until it closes (the run terminated).</summary>
    /// <param name="apiKey">The raw TestServer client bypasses Alba's default auth header, so the bearer is set here;
    /// defaults to the primary tenant.</param>
    public static async Task<List<Frame>> ReadToCloseAsync(IAlbaHost host, Guid runId, long? lastEventId, TimeSpan timeout, string apiKey = TestTenants.PrimaryKey)
    {
        using var client = host.GetTestServer().CreateClient();
        using var cts = new CancellationTokenSource(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/runs/{runId}/events", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Authorization", TestTenants.Bearer(apiKey));
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId.Value.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var frames = new List<Frame>();
        long id = 0;
        var eventName = "";
        var data = "";
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
        {
            if (line.Length == 0)
            {
                frames.Add(new Frame(id, eventName, data)); // blank line terminates a frame
                (id, eventName, data) = (0, "", "");
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = long.Parse(line[4..], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
        }

        return frames;
    }
}
