using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>Runs a builtin over its (unevaluated) argument nodes and the eval context. Most builtins evaluate all
/// arguments eagerly; a few (<c>coalesce</c>) are lazy, so the invoker receives nodes, not values.</summary>
/// <param name="args">The argument expression nodes, arity already validated at parse time.</param>
/// <param name="ctx">The scope + cancellation token.</param>
internal delegate ValueTask<object?> BuiltinInvoker(IReadOnlyList<ExpressionNode> args, EvalContext ctx);

/// <summary>One registered builtin: its name, the argument-count window the parser enforces (<c>unknown_function</c>
/// / <c>wrong_arity</c> are the static safety boundary), and the invoker.</summary>
/// <param name="Name">The function name as written in source.</param>
/// <param name="MinArity">Fewest arguments accepted (inclusive).</param>
/// <param name="MaxArity">Most arguments accepted (inclusive); <see cref="int.MaxValue"/> for variadic.</param>
/// <param name="Invoke">The evaluator.</param>
internal sealed record Builtin(string Name, int MinArity, int MaxArity, BuiltinInvoker Invoke);

/// <summary>
/// Runs a binding builtin (<c>filter</c>/<c>map</c>/<c>any</c>/<c>all</c>/<c>sortBy</c>, §7.2) — the fixed
/// <c>fn(source, v, body)</c> form whose middle argument is a scoped binding, not a value. The parser has already
/// validated the shape and extracted the binding name, so the invoker receives the source-list node, the binding
/// name, and the per-element body node (evaluated in a <see cref="BindingScope"/>).
/// </summary>
/// <param name="source">The node producing the list to iterate.</param>
/// <param name="binding">The per-element variable name introduced for <paramref name="body"/>.</param>
/// <param name="body">The predicate / projection / key node.</param>
/// <param name="ctx">The scope + cancellation token.</param>
internal delegate ValueTask<object?> BindingBuiltinInvoker(
    ExpressionNode source, string binding, ExpressionNode body, EvalContext ctx);

/// <summary>One registered binding builtin: its name and invoker. Arity (always 3) and the bare-identifier binding
/// slot are enforced structurally by the parser, so no arity window is stored.</summary>
/// <param name="Name">The function name as written in source.</param>
/// <param name="Invoke">The evaluator over the <c>(source, binding, body)</c> form.</param>
internal sealed record BindingBuiltin(string Name, BindingBuiltinInvoker Invoke);

/// <summary>
/// The enumerated builtin surface (§7.2) — the safety boundary. Only the Phase 1 fragment set is registered; the
/// registry shape (name → arity window → invoker) is exactly what Phase 2's additions slot into without
/// restructuring. Resolution is by exact name; an unknown name or an out-of-window arity is a parse-time failure.
/// </summary>
internal static partial class BuiltinRegistry
{
    private static Dictionary<string, Builtin> Registry { get; } = Build();

    private static Dictionary<string, BindingBuiltin> BindingRegistry { get; } = BuildBindings();

    /// <summary>Looks an ordinary builtin up by exact name.</summary>
    /// <param name="name">The function name from source.</param>
    /// <param name="builtin">The resolved builtin when found.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a registered ordinary builtin.</returns>
    public static bool TryGet(string name, out Builtin builtin) => Registry.TryGetValue(name, out builtin!);

    /// <summary>Looks a binding builtin (<c>filter</c>/<c>map</c>/<c>any</c>/<c>all</c>/<c>sortBy</c>) up by exact name.</summary>
    /// <param name="name">The function name from source.</param>
    /// <param name="binding">The resolved binding builtin when found.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a registered binding builtin.</returns>
    public static bool TryGetBinding(string name, out BindingBuiltin binding) => BindingRegistry.TryGetValue(name, out binding!);

    private static Dictionary<string, Builtin> Build()
    {
        var builtins = new[]
        {
            // String — null-propagate on the primary argument (§7.1).
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

            // URL — parsed as absolute System.Uri; invalid → invalid_url.
            Fn1("urlScheme", value => UrlPart(value, static uri => uri.Scheme)),
            Fn1("urlHost", value => UrlPart(value, static uri => uri.Host)),
            Fn1("urlPath", value => UrlPart(value, static uri => uri.AbsolutePath)),
            Fn2("resolveUrl", ResolveUrl),
            new Builtin("pageUrl", 0, 0, static (_, ctx) => new ValueTask<object?>(ctx.Scope.PageUrl())),

            // DOM — the only page access (§7.2). count is polymorphic (string ⇒ selector query).
            new Builtin("count", 1, 1, CountAsync),
            new Builtin("exists", 1, 2, ExistsAsync),
            new Builtin("text", 1, 2, DomString(static (dom, target, css, ct) => dom.TextAsync(target, css, ct))),
            new Builtin("innerText", 1, 2, DomString(static (dom, target, css, ct) => dom.InnerTextAsync(target, css, ct))),
            new Builtin("innerHtml", 1, 2, DomString(static (dom, target, css, ct) => dom.InnerHtmlAsync(target, css, ct))),
            new Builtin("attr", 2, 3, AttrAsync),
        };

        var map = new Dictionary<string, Builtin>(StringComparer.Ordinal);
        AddAll(map, builtins);
        AddAll(map, StringBuiltins());     // §7.2 string surface (BuiltinsString.cs)
        AddAll(map, CollectionBuiltins()); // §7.2 collection surface (BuiltinsCollection.cs)
        return map;
    }

    private static Dictionary<string, BindingBuiltin> BuildBindings()
    {
        var map = new Dictionary<string, BindingBuiltin>(StringComparer.Ordinal);
        foreach (var binding in BindingBuiltins()) // §7.2 binding surface (BuiltinsBinding.cs)
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

    private static BuiltinInvoker DomString(Func<IDomAccess, object, string?, CancellationToken, ValueTask<string?>> read) =>
        async (args, ctx) =>
        {
            var target = RequireDomTarget(await args[0].EvaluateAsync(ctx));
            var css = args.Count > 1 ? RequireString(await args[1].EvaluateAsync(ctx), "relative css") : null;
            return await read(ctx.Scope.Dom, target, css, ctx.Ct);
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
        var css = args.Count > 1 ? RequireString(await args[1].EvaluateAsync(ctx), "relative css") : null;
        return await ctx.Scope.Dom.ExistsAsync(target, css, ctx.Ct);
    }

    private static async ValueTask<object?> AttrAsync(IReadOnlyList<ExpressionNode> args, EvalContext ctx)
    {
        var target = RequireDomTarget(await args[0].EvaluateAsync(ctx));
        string? css;
        string name;
        if (args.Count == 3)
        {
            css = RequireString(await args[1].EvaluateAsync(ctx), "relative css");
            name = RequireString(await args[2].EvaluateAsync(ctx), "attribute name");
        }
        else
        {
            css = null;
            name = RequireString(await args[1].EvaluateAsync(ctx), "attribute name");
        }

        return await ctx.Scope.Dom.AttrAsync(target, css, name, ctx.Ct);
    }

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

    // resolveUrl(base, rel) = new Uri(new Uri(base), rel).ToString() — the reference's proper RFC resolution (:672,
    // §7.3), distinct from the search rows' naive scheme://host+href concat. base must be an absolute URL (else
    // invalid_url, like the other URL builtins); rel must be a string (else type_error); a malformed rel that the Uri
    // resolver rejects is invalid_url. NOT null-propagating — base is always present in the reference (input.link).
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

    private static string RequireString(object? value, string role) =>
        value is string s ? s : throw ExpressionValues.TypeError($"{role} must be a string, got {ExpressionValues.TypeName(value)}");
}
