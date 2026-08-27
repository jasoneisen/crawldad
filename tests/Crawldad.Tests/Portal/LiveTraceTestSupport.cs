using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Crawldad.Portal.Tenancy;

namespace Crawldad.Tests.Portal;

/// <summary>A programmable <see cref="ICircuitTenantResolver"/> for rendering the interactive live-trace component in
/// isolation — a fixed linked <see cref="PortalTenant"/> (whose client rides a scripted transport) or the not-linked
/// (null) state.</summary>
internal sealed class FakeCircuitTenantResolver(PortalTenant? tenant) : ICircuitTenantResolver
{
    public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tenant);
}

/// <summary>An HTTP response body that never completes until the caller's token is cancelled — so a buffered read (the
/// SDK's <c>GetRunTimelineAsync</c>) can be caught mid-flight by disposing the component. <see cref="Cancelled"/>
/// completes when the read observes the cancellation.</summary>
internal sealed class BlockingHttpContent : HttpContent
{
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the buffered read is cancelled by the consumer (the component disposed).</summary>
    public Task Cancelled => _cancelled.Task;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _cancelled.TrySetResult();
            throw;
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>A live, pushable <c>text/event-stream</c> response body: the test writes SSE frames one at a time (so the
/// component's render-per-frame can be observed), leaves the stream open (so a mid-stream disposal can be exercised),
/// then completes it cleanly or faults it to model a connection drop. <see cref="Cancelled"/> completes when the
/// consumer (the SDK, driven by the component's CancellationToken) tears the read down — the disposal-cancels-stream
/// proof.</summary>
internal sealed class PushSseContent : HttpContent
{
    private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PushSseContent() => Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");

    /// <summary>Completes once the stream read is cancelled by the consumer (i.e. the component disposed / reconnected).</summary>
    public Task Cancelled => _cancelled.Task;

    /// <summary>Writes one already-formatted SSE frame (e.g. <c>"id: 1\nevent: StepStarted\ndata: {}\n\n"</c>).</summary>
    public void Push(string frame) => _chunks.Writer.TryWrite(frame);

    /// <summary>Closes the stream cleanly at EOF (a tail close with no terminal frame → the component resumes).</summary>
    public void Complete() => _chunks.Writer.TryComplete();

    /// <summary>Faults the stream mid-read (a connection reset) → the component treats it as a transient drop.</summary>
    public void Fault(Exception error) => _chunks.Writer.TryComplete(error);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new NotSupportedException("The SDK reads the SSE body as a stream, not buffered.");

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new ChannelReadStream(_chunks.Reader, _cancelled));

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false; // unbounded / chunked
    }

    // A read-only stream over the pushed chunks: ReadAsync blocks (async) until the next chunk, returns 0 at a clean
    // close, throws the fault on a faulted close, and signals Cancelled when the consumer cancels.
    private sealed class ChannelReadStream(ChannelReader<string> reader, TaskCompletionSource cancelled) : Stream
    {
        private ReadOnlyMemory<byte> _remaining = ReadOnlyMemory<byte>.Empty;
        private CancellationTokenRegistration _registration;
        private bool _registered;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Signal teardown synchronously from Cancel() itself (the callback fires inline on the cancelling thread),
            // never from this read's continuation — which resumes on the Blazor dispatcher and would be stranded once the
            // component (and its renderer) is disposed. Registered once for the stream's life (the SDK reads with one
            // token), so cancellation is caught even between reads; an already-cancelled token fires it immediately.
            if (!_registered)
            {
                _registered = true;
                _registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
            }

            while (_remaining.IsEmpty)
            {
                if (!await reader.WaitToReadAsync(cancellationToken))
                {
                    return 0; // clean close → EOF
                }

                if (reader.TryRead(out var chunk))
                {
                    _remaining = Encoding.UTF8.GetBytes(chunk);
                }
            }

            var n = Math.Min(buffer.Length, _remaining.Length);
            _remaining.Span[..n].CopyTo(buffer.Span);
            _remaining = _remaining[n..];
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _registration.Dispose();
            base.Dispose(disposing);
        }
    }
}
