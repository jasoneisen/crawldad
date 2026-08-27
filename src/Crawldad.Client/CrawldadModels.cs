using System.Text.Json;
using Crawldad.Contracts.Runs;

namespace Crawldad.Client;

/// <summary>The outcome of <c>POST /runs</c> (or <c>replay</c>): the run either finished <b>synchronously</b> — HTTP
/// <c>200</c> carrying a terminal <see cref="RunResponse"/> — or was accepted onto the <b>async</b> surface — HTTP
/// <c>202</c> carrying a <see cref="RunStateResponse"/> (running, or queued behind the concurrency cap). Exactly one of
/// <see cref="Completed"/>/<see cref="Accepted"/> is non-null.</summary>
/// <param name="Completed">The terminal response when the run finished within the synchronous window; otherwise null.</param>
/// <param name="Accepted">The accepted async state (running/queued) when the run did not finish synchronously; otherwise null.</param>
public sealed record StartRunResult(RunResponse? Completed, RunStateResponse? Accepted)
{
    /// <summary>Whether the run finished synchronously (<see cref="Completed"/> is populated).</summary>
    public bool IsCompleted => Completed is not null;

    /// <summary>The run id, from whichever response is present — poll <c>GetRunAsync</c> or stream events with it.</summary>
    public Guid RunId => Completed?.RunId ?? Accepted!.RunId;

    /// <summary>The run's current disposition: a terminal status when synchronous, else <c>running</c>/<c>queued</c>.</summary>
    public RunStatus Status => Completed?.Status ?? Accepted!.Status;
}

/// <summary>One frame from a run's Server-Sent Events trace (<c>GET /runs/{id}/events</c>). <see cref="Id"/> is the
/// durable stream version — pass it back as <c>lastEventId</c> to resume exactly on reconnect. <see cref="EventType"/>
/// is the trace event's name (e.g. <c>RunStarted</c>, <c>StepStarted</c>, <c>Navigated</c>, <c>RunSucceeded</c>);
/// <see cref="Data"/> is the raw, already-scrubbed JSON body. Keepalive comment frames are not surfaced.</summary>
/// <param name="Id">The frame id (stream version), or null if the server omitted it.</param>
/// <param name="EventType">The SSE <c>event:</c> name — the trace event's type name.</param>
/// <param name="Data">The raw JSON <c>data:</c> payload for the frame.</param>
public sealed record RunEventFrame(long? Id, string EventType, string Data)
{
    /// <summary>Whether this frame is a terminal event (<c>RunSucceeded</c>/<c>RunFailed</c>/<c>RunCancelled</c>) —
    /// the last frame before the stream closes.</summary>
    public bool IsTerminal =>
        EventType is "RunSucceeded" or "RunFailed" or "RunCancelled";

    /// <summary>Deserializes the frame's <see cref="Data"/> into <typeparamref name="T"/> using the shared wire
    /// conventions. The concrete trace-event shapes are documented in <c>docs/API.md §6</c>; terminal frames carry
    /// <c>{ stats, finishedAt }</c> (and a <c>failure</c> on <c>RunFailed</c>).</summary>
    /// <typeparam name="T">The shape to bind the frame data to.</typeparam>
    /// <returns>The deserialized value, or default when the data is JSON null.</returns>
    public T? DataAs<T>() => JsonSerializer.Deserialize<T>(Data, CrawldadJson.Options);
}

/// <summary>A captured screenshot fetched by ref (<c>GET /runs/{id}/screenshots/{reference}</c>): the PNG
/// <see cref="Content"/> bytes, the <see cref="ContentType"/> (<c>image/png</c>), and the content-addressed
/// <see cref="ETag"/> (the blob digest) for conditional revalidation.</summary>
/// <param name="Content">The raw PNG bytes.</param>
/// <param name="ContentType">The response content type (<c>image/png</c>).</param>
/// <param name="ETag">The entity tag (the content digest), when present.</param>
public sealed record ScreenshotContent(ReadOnlyMemory<byte> Content, string ContentType, string? ETag);
