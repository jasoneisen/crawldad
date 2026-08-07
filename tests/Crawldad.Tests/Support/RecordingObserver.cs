using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Tests.Support;

/// <summary>
/// A white-box <see cref="IRunObserver"/> for interpreter unit tests: captures the trace events the interpreter emits
/// (§13 step trace + coarse log/attempt events) and the checkpoints it reaches, and can force a cooperative cancel. It does
/// no persistence — the executor's real observer is exercised by the durable/SSE integration tests; this one lets a unit
/// test assert the interpreter emits the right events in order without a database.
/// </summary>
internal sealed class RecordingObserver : IRunObserver
{
    private readonly List<object> _events = [];

    /// <summary>When true, the interpreter's between-steps check trips a cooperative cancel.</summary>
    public bool Cancel { get; set; }

    /// <inheritdoc />
    public bool CancellationRequested => Cancel;

    /// <summary>The trace events the interpreter emitted, in occurrence order.</summary>
    public IReadOnlyList<object> Events => _events;

    /// <summary>The checkpoints the interpreter reached, in order.</summary>
    public List<CheckpointSnapshot> Checkpoints { get; } = [];

    /// <inheritdoc />
    public ValueTask EmitAsync(object traceEvent, CancellationToken ct)
    {
        _events.Add(traceEvent);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask CheckpointReachedAsync(CheckpointSnapshot checkpoint, CancellationToken ct)
    {
        Checkpoints.Add(checkpoint);
        return ValueTask.CompletedTask;
    }
}
