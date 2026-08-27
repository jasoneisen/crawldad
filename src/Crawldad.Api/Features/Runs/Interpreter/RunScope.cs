using Crawldad.Api.Features.Runs.Interpreter.Expressions;
using Crawldad.Api.Infrastructure.Browser;

namespace Crawldad.Api.Features.Runs.Interpreter;

/// <summary>The one flat, mutable run scope: <c>input.*</c> (read-only map), declared <c>vars</c>, and everything
/// <c>set</c>/<c>push</c> create — plus the live page. Implements <see cref="IEvalScope"/> and <see cref="IDomAccess"/>
/// (expressions never mutate — mutation is structural). Loop variables <see cref="Shadow"/> outer names and unshadow on exit.</summary>
internal sealed class RunScope : IEvalScope, IDomAccess
{
    private readonly Dictionary<string, object?> _vars = new(StringComparer.Ordinal);
    private readonly SelResolver _sel;
    private readonly ISelectorMissSink _misses;
    private IPageHandle? _page;

    public RunScope(
        IReadOnlyDictionary<string, object?> input,
        int expressionStepBudget = CrawldadExpression.DefaultStepBudget,
        ISelectorMissSink? misses = null)
    {
        _vars["input"] = new Dictionary<string, object?>(input, StringComparer.Ordinal);
        _sel = new SelResolver(this);
        _misses = misses ?? NoSelectorMissSink.Instance; // the interpreter passes its counter-backed sink; scope/selector unit tests get the inert one
        ExpressionStepBudget = expressionStepBudget;
    }

    /// <summary>The per-evaluation expression fuel budget every expression this run evaluates is metered against —
    /// the server-configured knob threaded from <see cref="RunLimits"/>, overriding the interface default so a payload
    /// can never widen it.</summary>
    public int ExpressionStepBudget { get; }

    /// <summary>The current page. Bound after the backend connects; reads before <see cref="Bind"/> throw.</summary>
    internal IPageHandle PageHandle =>
        _page ?? throw new InvalidOperationException("the page is not bound yet (no backend connected)");

    /// <summary>The shared selector resolver over this scope (node selectors and structured Sel maps).</summary>
    internal SelResolver Sel => _sel;

    /// <summary>The current variable bindings (including <c>input</c>), for snapshotting the accumulated state at a
    /// checkpoint. A read-only view — mutation stays structural (<see cref="Set"/>/<see cref="Push"/>).</summary>
    internal IReadOnlyDictionary<string, object?> Vars => _vars;

    /// <summary>Binds the live page once the backend has connected.</summary>
    internal void Bind(IPageHandle page) => _page = page;

    // ----- IEvalScope --------------------------------------------------------

    public bool TryResolve(string name, out object? value) => _vars.TryGetValue(name, out value);

    public string PageUrl() => PageHandle.Url;

    public IDomAccess Dom => this;

    public ISelectorMissSink Misses => _misses;

    // ----- mutation (structural nodes only) ----------------------------------

    /// <summary>Binds or rebinds a variable in the run scope (backs <c>set</c> and <c>vars</c>).</summary>
    public void Set(string name, object? value) => _vars[name] = value;

    /// <summary>Appends to an array variable (backs <c>push</c>). Undefined or non-array target is terminal.</summary>
    public void Push(string into, object? value)
    {
        if (!_vars.TryGetValue(into, out var target) || target is not List<object?> list)
        {
            throw new InterpreterException(
                InterpreterErrorCodes.UndefinedPushTarget, $"push target '{into}' is not a defined array");
        }

        list.Add(value);
    }

    /// <summary>Shadows <paramref name="bindings"/> (loop variables) for the duration of the returned scope, restoring
    /// the prior bindings on dispose. Each iteration <see cref="Set"/>s the loop var inside this shadow.</summary>
    public IDisposable Shadow(params (string Name, object? Value)[] bindings)
    {
        var saved = new (string Name, bool Had, object? Old)[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            var had = _vars.TryGetValue(bindings[i].Name, out var old);
            saved[i] = (bindings[i].Name, had, old);
            _vars[bindings[i].Name] = bindings[i].Value;
        }

        return new ShadowScope(this, saved);
    }

    // ----- IDomAccess (read-only page access) ---------------------------

    public async ValueTask<long> CountAsync(object target, string? relativeCss, CancellationToken ct) =>
        await _sel.ResolveTarget(target, relativeCss).CountAsync(ct);

    public async ValueTask<bool> ExistsAsync(object target, string? relativeCss, CancellationToken ct) =>
        await _sel.ResolveTarget(target, relativeCss).CountAsync(ct) > 0;

    public async ValueTask<string?> TextAsync(object target, string? relativeCss, CancellationToken ct) =>
        await _sel.ResolveTarget(target, relativeCss).TextContentAsync(ct);

    public ValueTask<string?> InnerTextAsync(object target, string? relativeCss, CancellationToken ct) =>
        NullablyAsync(target, relativeCss, static (h, c) => h.InnerTextAsync(c), ct);

    public ValueTask<string?> InnerHtmlAsync(object target, string? relativeCss, CancellationToken ct) =>
        NullablyAsync(target, relativeCss, static (h, c) => h.InnerHTMLAsync(c), ct);

    public async ValueTask<string?> AttrAsync(object target, string? relativeCss, string name, CancellationToken ct) =>
        await _sel.ResolveTarget(target, relativeCss).GetAttributeAsync(name, ct);

    // innerText/innerHTML are non-null at the seam, so null-propagate here when nothing matches.
    private async ValueTask<string?> NullablyAsync(
        object target, string? relativeCss, Func<ILocatorHandle, CancellationToken, Task<string>> read, CancellationToken ct)
    {
        var handle = _sel.ResolveTarget(target, relativeCss);
        return await handle.CountAsync(ct) == 0 ? null : await read(handle, ct);
    }

    private sealed class ShadowScope(RunScope scope, (string Name, bool Had, object? Old)[] saved) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (name, had, old) in saved)
            {
                if (had)
                {
                    scope._vars[name] = old;
                }
                else
                {
                    scope._vars.Remove(name);
                }
            }
        }
    }
}
