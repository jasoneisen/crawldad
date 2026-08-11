namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>Where an extraction builtin (<c>text</c>/<c>innerText</c>/<c>innerHtml</c>/<c>attr</c>) reports a
/// <b>selector miss</b> — its target matched zero elements (distinct from a matched-but-empty element, which is
/// legitimately blank data, not a miss). The run scope backs this with the interpreter's counter + trace stream; the
/// no-op <see cref="NoSelectorMissSink"/> is used by scopes that don't track (isolated expression tests).</summary>
public interface ISelectorMissSink
{
    /// <summary>Records one selector miss and reports whether it must be terminal. Always increments the run's
    /// <c>selectorMisses</c> stat and (on the first miss of this exact <paramref name="selector"/> in the run) emits a
    /// <c>SelectorMiss</c> trace event — the always-on soft signal. Returns true when the miss must fail the run with a
    /// classified <c>selector_miss</c>: either the extraction was <c>require(...)</c>-wrapped (<paramref name="required"/>)
    /// or <c>config.strictExtraction</c> promotes every miss to terminal.</summary>
    ValueTask<bool> RecordAsync(string selector, bool required, CancellationToken ct);
}

/// <summary>The inert sink for scopes with no run behind them (isolated expression tests): it counts and emits nothing,
/// but still honours <c>require(...)</c> so a required miss is terminal even without an interpreter — a soft miss is a
/// silent null.</summary>
internal sealed class NoSelectorMissSink : ISelectorMissSink
{
    /// <summary>The shared inert instance.</summary>
    public static ISelectorMissSink Instance { get; } = new NoSelectorMissSink();

    public ValueTask<bool> RecordAsync(string selector, bool required, CancellationToken ct) => new(required);
}
