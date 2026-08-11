using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>Runs a builtin over its (unevaluated) argument nodes; most evaluate eagerly, but <c>coalesce</c> is lazy.
/// Argument arity is already validated at parse time.</summary>
internal delegate ValueTask<object?> BuiltinInvoker(IReadOnlyList<ExpressionNode> args, EvalContext ctx);

/// <summary>One registered builtin: name, the parser-enforced arity window (<c>MaxArity</c> = <see cref="int.MaxValue"/>
/// for variadic; violations are <c>unknown_function</c>/<c>wrong_arity</c>), and the invoker.</summary>
internal sealed record Builtin(string Name, int MinArity, int MaxArity, BuiltinInvoker Invoke);

/// <summary>A binding builtin's invoker: <c>fn(source, v, body)</c> whose middle argument is a scoped binding, not a
/// value, already validated and extracted by the parser. Receives the source node, binding name, and body node
/// (evaluated in a <see cref="BindingScope"/>).</summary>
internal delegate ValueTask<object?> BindingBuiltinInvoker(
    ExpressionNode source, string binding, ExpressionNode body, EvalContext ctx);

/// <summary>One registered binding builtin: its name and invoker. Arity (always 3) and the bare-identifier binding
/// slot are enforced structurally by the parser, so no arity window is stored.</summary>
internal sealed record BindingBuiltin(string Name, BindingBuiltinInvoker Invoke);

/// <summary>The registered builtin surface: name → arity window → invoker, resolved by exact name. An unknown name or
/// out-of-window arity is a parse-time failure.</summary>
internal static partial class BuiltinRegistry
{
    private static Dictionary<string, Builtin> Registry { get; } = Build();

    private static Dictionary<string, BindingBuiltin> BindingRegistry { get; } = BuildBindings();

    /// <summary>Looks an ordinary builtin up by exact name.</summary>
    public static bool TryGet(string name, out Builtin builtin) => Registry.TryGetValue(name, out builtin!);

    /// <summary>Looks a binding builtin (<c>filter</c>/<c>map</c>/<c>any</c>/<c>all</c>/<c>sortBy</c>) up by exact name.</summary>
    public static bool TryGetBinding(string name, out BindingBuiltin binding) => BindingRegistry.TryGetValue(name, out binding!);

    private static Dictionary<string, Builtin> Build()
    {
        var builtins = new[]
        {
            // String — null-propagates on the primary argument.
            Fn1("isNullOrWhitespace", IsNullOrWhitespace),
            Fn1("trim", Trim),
            Fn1("lower", Lower),
            Fn1("upper", Upper),
            Fn1("string", ExpressionValues.ToStringValue),
            Fn1("length", Length),
            Fn2("startsWith", StartsWith),
            Fn2("contains", Contains),
            Fn1("toInt", ToInt),
            Fn1("isInt", IsInt),

            // Collection / control.
            new Builtin("coalesce", 2, int.MaxValue, CoalesceAsync),
            new Builtin("require", 1, 1, RequireAsync),

            // URL — parsed as absolute System.Uri; invalid → invalid_url.
            Fn1("urlScheme", value => UrlPart(value, static uri => uri.Scheme)),
            Fn1("urlHost", value => UrlPart(value, static uri => uri.Host)),
            Fn1("urlPath", value => UrlPart(value, static uri => uri.AbsolutePath)),
            Fn2("resolveUrl", ResolveUrl),
            new Builtin("pageUrl", 0, 0, static (_, ctx) => new ValueTask<object?>(ctx.Scope.PageUrl())),

            // DOM — the only page access. count/exists are existence predicates (never selector misses); the extraction
            // builtins text/innerText/innerHtml/attr record a miss when their target matches no element.
            new Builtin("count", 1, 1, CountAsync),
            new Builtin("exists", 1, 2, ExistsAsync),
            new Builtin("text", 1, 2, DomExtract(static (dom, target, css, ct) => dom.TextAsync(target, css, ct))),
            new Builtin("innerText", 1, 2, DomExtract(static (dom, target, css, ct) => dom.InnerTextAsync(target, css, ct))),
            new Builtin("innerHtml", 1, 2, DomExtract(static (dom, target, css, ct) => dom.InnerHtmlAsync(target, css, ct))),
            new Builtin("attr", 2, 3, AttrAsync),
        };

        var map = new Dictionary<string, Builtin>(StringComparer.Ordinal);
        AddAll(map, builtins);
        AddAll(map, StringBuiltins());     // string surface (BuiltinsString.cs)
        AddAll(map, CollectionBuiltins()); // collection surface (BuiltinsCollection.cs)
        return map;
    }

    private static Dictionary<string, BindingBuiltin> BuildBindings()
    {
        var map = new Dictionary<string, BindingBuiltin>(StringComparer.Ordinal);
        foreach (var binding in BindingBuiltins()) // binding surface (BuiltinsBinding.cs)
        {
            map.Add(binding.Name, binding);
        }

        return map;
    }

    private static void AddAll(Dictionary<string, Builtin> map, IEnumerable<Builtin> builtins)
    {
        foreach (var builtin in builtins)
        {
            map.Add(builtin.Name, builtin);
        }
    }

    // ----- invoker factories -------------------------------------------------

    private static Builtin Fn1(string name, Func<object?, object?> fn) =>
        new(name, 1, 1, async (args, ctx) => fn(await args[0].EvaluateAsync(ctx)));

    private static Builtin Fn2(string name, Func<object?, object?, object?> fn) =>
        new(name, 2, 2, async (args, ctx) => fn(await args[0].EvaluateAsync(ctx), await args[1].EvaluateAsync(ctx)));

    private static Builtin Fn3(string name, Func<object?, object?, object?, object?> fn) =>
        new(name, 3, 3, async (args, ctx) =>
            fn(await args[0].EvaluateAsync(ctx), await args[1].EvaluateAsync(ctx), await args[2].EvaluateAsync(ctx)));

    // A DOM extraction builtin (text/innerText/innerHtml): reads the first match's string, and reports a selector miss
    // when NOTHING matched. All three read null exactly when the target matched zero elements (a matched-but-empty
    // element is "", never null — both backends short-circuit to null on a zero count), so the null return IS the miss
    // signal, needing no extra DOM round-trip. A soft miss still null-propagates as before; a required/strict miss throws.
    private static BuiltinInvoker DomExtract(Func<IDomAccess, object, string?, CancellationToken, ValueTask<string?>> read) =>
        async (args, ctx) =>
        {
            var target = RequireDomTarget(await args[0].EvaluateAsync(ctx));
            var css = args.Count > 1 ? ExpressionValues.RequireString(await args[1].EvaluateAsync(ctx), "relative css") : null;
            var value = await read(ctx.Scope.Dom, target, css, ctx.Ct);
            if (value is null)
            {
                await ReportMissAsync(ctx, target, css);
            }

            return value;
        };

    // ----- lazy / polymorphic invokers --------------------------------------

    private static async ValueTask<object?> CoalesceAsync(IReadOnlyList<ExpressionNode> args, EvalContext ctx)
    {
        foreach (var arg in args)
        {
            var value = await arg.EvaluateAsync(ctx);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static async ValueTask<object?> CountAsync(IReadOnlyList<ExpressionNode> args, EvalContext ctx)
    {
        var value = await args[0].EvaluateAsync(ctx);
        switch (value)
        {
            case string selector:
                return await ctx.Scope.Dom.CountAsync(selector, null, ctx.Ct);
            case List<object?> list:
                return (long)list.Count;
            case Dictionary<string, object?> map:
                return (long)map.Count;
            case null:
                throw ExpressionValues.TypeError("count(null) is not allowed");
            case bool or long or double:
                throw ExpressionValues.TypeError($"count expects a selector, array, map, or handle, got {ExpressionValues.TypeName(value)}");
            default:
                return await ctx.Scope.Dom.CountAsync(value, null, ctx.Ct);
        }
    }

    private static async ValueTask<object?> ExistsAsync(IReadOnlyList<ExpressionNode> args, EvalContext ctx)
    {
        var target = RequireDomTarget(await args[0].EvaluateAsync(ctx));
        var css = args.Count > 1 ? ExpressionValues.RequireString(await args[1].EvaluateAsync(ctx), "relative css") : null;
        return await ctx.Scope.Dom.ExistsAsync(target, css, ctx.Ct);
    }

    private static async ValueTask<object?> AttrAsync(IReadOnlyList<ExpressionNode> args, EvalContext ctx)
    {
        var target = RequireDomTarget(await args[0].EvaluateAsync(ctx));
        string? css;
        string name;
        if (args.Count == 3)
        {
            css = ExpressionValues.RequireString(await args[1].EvaluateAsync(ctx), "relative css");
            name = ExpressionValues.RequireString(await args[2].EvaluateAsync(ctx), "attribute name");
        }
        else
        {
            css = null;
            name = ExpressionValues.RequireString(await args[1].EvaluateAsync(ctx), "attribute name");
        }

        var value = await ctx.Scope.Dom.AttrAsync(target, css, name, ctx.Ct);

        // attr's null is ambiguous — no element matched, OR a matched element simply lacks the attribute (legitimately
        // blank, NOT a miss). Only a zero count is a miss, so disambiguate with a count, and only on the null path so a
        // present attribute costs no extra DOM round-trip.
        if (value is null && await ctx.Scope.Dom.CountAsync(target, css, ctx.Ct) == 0)
        {
            await ReportMissAsync(ctx, target, css);
        }

        return value;
    }

    // Reports one selector miss for an extraction builtin: records it on the run's sink (which counts it and, first time
    // for this selector, emits a SelectorMiss event) and raises a terminal selector_miss when the sink says so — because
    // the extraction was require()-wrapped (ctx.RequireExtraction) or config.strictExtraction promotes every miss.
    private static async ValueTask ReportMissAsync(EvalContext ctx, object target, string? relativeCss)
    {
        var selector = DescribeSelector(target, relativeCss);
        if (await ctx.Scope.Misses.RecordAsync(selector, ctx.RequireExtraction, ctx.Ct))
        {
            throw new ExpressionEvaluationException(
                ExpressionErrorCodes.SelectorMiss, $"required extraction found no element matching selector '{selector}'");
        }
    }

    // A stable, human-readable description of the missed target for the SelectorMiss event and its dedupe key: the CSS
    // string as-is, a structured Sel's primary locator, or a placeholder for an opaque handle (a bound locator carries no
    // stable text) — narrowed by the relative CSS when present, which is the part that drifts in a per-row extraction.
    private static string DescribeSelector(object target, string? relativeCss)
    {
        var baseSelector = target switch
        {
            string css => css,
            Dictionary<string, object?> map => DescribeSelMap(map),
            _ => "<handle>",
        };

        return relativeCss is null ? baseSelector : $"{baseSelector} {relativeCss}";
    }

    // The primary locator of a structured Sel map, in resolution-priority order, for the miss description: the bare css
    // string (the common case), or a "<kind>=<value>" for the other roots, or a placeholder for a locator-less map.
    private static string DescribeSelMap(Dictionary<string, object?> map)
    {
        foreach (var key in _selMapLocatorKeys)
        {
            if (map.TryGetValue(key, out var value))
            {
                var text = ExpressionValues.ToStringValue(value);
                return string.Equals(key, "css", StringComparison.Ordinal) ? text : $"{key}={text}";
            }
        }

        return "<sel>";
    }

    private static readonly string[] _selMapLocatorKeys = ["css", "xpath", "text", "role", "title", "base"];

    // require(x): evaluates x with selector misses in its subtree promoted to terminal selector_miss failures. A lazy
    // builtin (like coalesce) so it can rewrite the evaluation context, not just receive x's value — composes with
    // trim/coalesce/binding builtins, which thread the flag through their ctx copies. With no extraction inside, it is a
    // transparent passthrough.
    private static ValueTask<object?> RequireAsync(IReadOnlyList<ExpressionNode> args, EvalContext ctx) =>
        args[0].EvaluateAsync(ctx with { RequireExtraction = true });

    // ----- value-level helpers ----------------------------------------------

    private static object? IsNullOrWhitespace(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        _ => throw ExpressionValues.TypeError($"isNullOrWhitespace expects a string, got {ExpressionValues.TypeName(value)}"),
    };

    private static object? Trim(object? value) => value switch
    {
        null => null,
        string s => s.Trim(),
        _ => throw ExpressionValues.TypeError($"trim expects a string, got {ExpressionValues.TypeName(value)}"),
    };

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "lower() is a user-facing builtin whose contract is to lowercase (reproducing the reference's .ToLower()); it is not a normalization step.")]
    private static object? Lower(object? value) => value switch
    {
        null => null,
        string s => s.ToLowerInvariant(),
        _ => throw ExpressionValues.TypeError($"lower expects a string, got {ExpressionValues.TypeName(value)}"),
    };

    private static object? Upper(object? value) => value switch
    {
        null => null,
        string s => s.ToUpperInvariant(),
        _ => throw ExpressionValues.TypeError($"upper expects a string, got {ExpressionValues.TypeName(value)}"),
    };

    private static object? Length(object? value) => value switch
    {
        null => null,
        string s => (long)s.Length,
        List<object?> list => (long)list.Count,
        _ => throw ExpressionValues.TypeError($"length expects a string or array, got {ExpressionValues.TypeName(value)}"),
    };

    private static object? StartsWith(object? value, object? prefix)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s && prefix is string p)
        {
            return s.StartsWith(p, StringComparison.Ordinal);
        }

        throw ExpressionValues.TypeError(
            $"startsWith expects strings, got {ExpressionValues.TypeName(value)} and {ExpressionValues.TypeName(prefix)}");
    }

    private static object? Contains(object? value, object? substring)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s && substring is string x)
        {
            return s.Contains(x, StringComparison.Ordinal);
        }

        throw ExpressionValues.TypeError(
            $"contains expects strings, got {ExpressionValues.TypeName(value)} and {ExpressionValues.TypeName(substring)}");
    }

    private static object? ToInt(object? value)
    {
        if (value is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new ExpressionEvaluationException(
            ExpressionErrorCodes.IntConversionFailed, $"cannot convert {ExpressionValues.TypeName(value)} to int");
    }

    private static object? IsInt(object? value) =>
        value is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static string UrlPart(object? value, Func<Uri, string> part)
    {
        if (value is string s && Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            return part(uri);
        }

        throw new ExpressionEvaluationException(
            ExpressionErrorCodes.InvalidUrl, $"not a valid absolute URL (got {ExpressionValues.TypeName(value)})");
    }

    // resolveUrl(base, rel) = new Uri(new Uri(base), rel).ToString() — proper RFC resolution, not naive concatenation.
    // base must be an absolute URL (else invalid_url); rel must be a string (else type_error); a malformed rel is
    // invalid_url. NOT null-propagating — base is always present.
    private static string ResolveUrl(object? baseValue, object? relValue)
    {
        if (baseValue is not string baseText || !Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri))
        {
            throw new ExpressionEvaluationException(
                ExpressionErrorCodes.InvalidUrl, $"resolveUrl base is not a valid absolute URL (got {ExpressionValues.TypeName(baseValue)})");
        }

        if (relValue is not string rel)
        {
            throw ExpressionValues.TypeError($"resolveUrl relative must be a string, got {ExpressionValues.TypeName(relValue)}");
        }

        try
        {
            return new Uri(baseUri, rel).ToString();
        }
        catch (UriFormatException)
        {
            throw new ExpressionEvaluationException(
                ExpressionErrorCodes.InvalidUrl, $"resolveUrl could not resolve '{rel}' against '{baseText}'");
        }
    }

    // ----- DOM target / argument validation ---------------------------------

    private static object RequireDomTarget(object? value) => value switch
    {
        string s => s,
        Dictionary<string, object?> map => map,
        null or bool or long or double or List<object?> =>
            throw ExpressionValues.TypeError($"DOM target must be a selector, handle, or Sel map, got {ExpressionValues.TypeName(value)}"),
        _ => value, // opaque locator handle
    };
}
