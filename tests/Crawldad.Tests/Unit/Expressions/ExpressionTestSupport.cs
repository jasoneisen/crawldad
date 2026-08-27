using Crawldad.Api.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>One DOM read the fake observed, so tests can assert the interpreter passed the right target/css through.</summary>
internal sealed record DomCall(string Op, object Target, string? Css, string? Name);

/// <summary>A scriptable <see cref="IDomAccess"/>: each operation delegates to a settable lambda (keyed off target/css)
/// and every call is recorded. Defaults return "nothing there" so a test only wires the reads it cares about.</summary>
internal sealed class FakeDom : IDomAccess
{
    public Func<object, string?, long> OnCount { get; set; } = static (_, _) => 0L;

    public Func<object, string?, bool> OnExists { get; set; } = static (_, _) => false;

    public Func<object, string?, string?> OnText { get; set; } = static (_, _) => null;

    public Func<object, string?, string?> OnInnerText { get; set; } = static (_, _) => null;

    public Func<object, string?, string?> OnInnerHtml { get; set; } = static (_, _) => null;

    public Func<object, string?, string, string?> OnAttr { get; set; } = static (_, _, _) => null;

    public List<DomCall> Calls { get; } = [];

    public ValueTask<long> CountAsync(object target, string? relativeCss, CancellationToken ct)
    {
        Calls.Add(new DomCall("count", target, relativeCss, null));
        return new ValueTask<long>(OnCount(target, relativeCss));
    }

    public ValueTask<bool> ExistsAsync(object target, string? relativeCss, CancellationToken ct)
    {
        Calls.Add(new DomCall("exists", target, relativeCss, null));
        return new ValueTask<bool>(OnExists(target, relativeCss));
    }

    public ValueTask<string?> TextAsync(object target, string? relativeCss, CancellationToken ct)
    {
        Calls.Add(new DomCall("text", target, relativeCss, null));
        return new ValueTask<string?>(OnText(target, relativeCss));
    }

    public ValueTask<string?> InnerTextAsync(object target, string? relativeCss, CancellationToken ct)
    {
        Calls.Add(new DomCall("innerText", target, relativeCss, null));
        return new ValueTask<string?>(OnInnerText(target, relativeCss));
    }

    public ValueTask<string?> InnerHtmlAsync(object target, string? relativeCss, CancellationToken ct)
    {
        Calls.Add(new DomCall("innerHtml", target, relativeCss, null));
        return new ValueTask<string?>(OnInnerHtml(target, relativeCss));
    }

    public ValueTask<string?> AttrAsync(object target, string? relativeCss, string name, CancellationToken ct)
    {
        Calls.Add(new DomCall("attr", target, relativeCss, name));
        return new ValueTask<string?>(OnAttr(target, relativeCss, name));
    }
}

/// <summary>A scriptable <see cref="ISelectorMissSink"/> recording every reported miss, so builtin tests can assert the
/// described selector and its required flag. <see cref="Strict"/> models <c>config.strictExtraction</c> (every miss
/// terminal); otherwise a miss is terminal only when the extraction was <c>require(...)</c>-wrapped.</summary>
internal sealed class RecordingMissSink : ISelectorMissSink
{
    public bool Strict { get; set; }

    public List<(string Selector, bool Required)> Records { get; } = [];

    public ValueTask<bool> RecordAsync(string selector, bool required, CancellationToken ct)
    {
        Records.Add((selector, required));
        return new ValueTask<bool>(required || Strict);
    }
}

/// <summary>A flat <see cref="IEvalScope"/> over a mutable var bag, a fixed page URL, a <see cref="FakeDom"/>, and a
/// recording <see cref="ISelectorMissSink"/> (so selector-miss/require behaviour is testable without an interpreter).</summary>
internal sealed class FakeScope : IEvalScope
{
    private readonly Dictionary<string, object?> _vars = new(StringComparer.Ordinal);
    private readonly string _pageUrl;

    public FakeScope(IDomAccess? dom = null, string pageUrl = "https://example.com/", ISelectorMissSink? misses = null)
    {
        Dom = dom ?? new FakeDom();
        _pageUrl = pageUrl;
        Misses = misses ?? new RecordingMissSink();
    }

    public IDomAccess Dom { get; }

    public ISelectorMissSink Misses { get; }

    public string PageUrl() => _pageUrl;

    public bool TryResolve(string name, out object? value) => _vars.TryGetValue(name, out value);

    public FakeScope With(string name, object? value)
    {
        _vars[name] = value;
        return this;
    }
}

/// <summary>An arbitrary non-value-model object used as an opaque locator handle in tests.</summary>
internal sealed class FakeHandle
{
    public override string ToString() => "«handle»";
}

/// <summary>Terse helpers so the many small cases read as one-liners.</summary>
internal static class Xp
{
    public static ValueTask<object?> EvalAsync(string source, IEvalScope? scope = null) =>
        CrawldadExpression.Parse(source).EvaluateAsync(scope ?? new FakeScope());

    public static async Task<ExpressionEvaluationException> EvalErrorAsync(string source, IEvalScope? scope = null) =>
        await Should.ThrowAsync<ExpressionEvaluationException>(
            async () => await CrawldadExpression.Parse(source).EvaluateAsync(scope ?? new FakeScope()));

    public static ExpressionParseException ParseError(string source) =>
        Should.Throw<ExpressionParseException>(() => CrawldadExpression.Parse(source));
}

/// <summary>Value-model constructors so tests can seed arrays/maps into a <see cref="FakeScope"/> concisely.</summary>
internal static class Val
{
    public static List<object?> List(params object?[] items) => [.. items];

    public static Dictionary<string, object?> Map(params (string Key, object? Value)[] entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            map[key] = value;
        }

        return map;
    }
}
