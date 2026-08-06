using System.Text.Json;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Crawldad.Web.Infrastructure.Browser;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// Resolves selectors to lazy <see cref="ILocatorHandle"/>s (§5.2). One resolver serves three shapes: DOM-access
/// targets from expressions (a CSS string, an opaque handle, or a structured <c>Sel</c> map), and node selectors from
/// the payload JSON (a string with <b>var-first</b> precedence — a bound handle var wins over CSS — or a structured
/// object). Structured resolution chains seam calls for <c>css|title|base|nth|first|filter.hasTextRegex</c>; frames
/// (<c>in</c>) are a terminal <c>not_supported_in_v0</c> in v0. Malformed structured selectors surface as terminal
/// failures at execution (save-time validation is Phase 3).
/// </summary>
internal sealed class SelResolver(RunScope scope)
{
    /// <summary>Resolves a DOM-access target (CSS string | handle | structured map) and applies an optional relative CSS.</summary>
    /// <param name="target">The evaluated target value.</param>
    /// <param name="relativeCss">A child CSS narrowing the target, or null.</param>
    public ILocatorHandle ResolveTarget(object target, string? relativeCss)
    {
        var handle = ResolveBase(target);
        return relativeCss is null ? handle : handle.Locator(relativeCss);
    }

    private ILocatorHandle ResolveBase(object target)
    {
        if (target is string css)
        {
            return scope.PageHandle.Locator(css);
        }

        if (target is Dictionary<string, object?> map)
        {
            return ResolveMap(map);
        }

        return (ILocatorHandle)target; // opaque locator handle (the only other value-model shape reads accept)
    }

    /// <summary>Resolves a structured <c>Sel</c> map (values already evaluated) by chaining seam refinements.</summary>
    /// <param name="map">The structured selector map.</param>
    public ILocatorHandle ResolveMap(Dictionary<string, object?> map)
    {
        if (map.ContainsKey("in"))
        {
            throw NotSupported("frames ('in') are not supported in v0");
        }

        var handle = ResolveRoot(map);

        if (map.TryGetValue("filter", out var filter))
        {
            handle = handle.Filter((string)((Dictionary<string, object?>)filter!)["hasTextRegex"]!);
        }

        if (map.TryGetValue("nth", out var nth))
        {
            handle = handle.Nth((int)(long)nth!);
        }

        // `first` is always a bool when present (from an object literal or GetBoolean); the direct unbox avoids a
        // dead is-bool branch on well-formed input.
        if (map.TryGetValue("first", out var first) && (bool)first!)
        {
            handle = handle.First;
        }

        return handle;
    }

    private ILocatorHandle ResolveRoot(Dictionary<string, object?> map)
    {
        if (map.TryGetValue("base", out var baseVar))
        {
            var baseHandle = RequireHandle((string)baseVar!);
            return map.TryGetValue("css", out var relCss) ? baseHandle.Locator((string)relCss!) : baseHandle;
        }

        if (map.TryGetValue("css", out var css))
        {
            return scope.PageHandle.Locator((string)css!);
        }

        if (map.TryGetValue("title", out var title))
        {
            return scope.PageHandle.GetByTitle((string)title!);
        }

        throw new InterpreterException(InterpreterErrorCodes.MalformedNode, "a Sel object needs one of 'css', 'title', or 'base'");
    }

    /// <summary>Resolves a variable name that must hold a bound locator handle (<c>locate</c> <c>from</c>/<c>base</c>).</summary>
    /// <param name="name">The variable name.</param>
    /// <exception cref="InterpreterException">When the name is unbound or not a handle.</exception>
    internal ILocatorHandle RequireHandle(string name) =>
        scope.TryResolve(name, out var value) && value is ILocatorHandle handle
            ? handle
            : throw new InterpreterException(InterpreterErrorCodes.MalformedNode, $"'{name}' is not a bound locator handle");

    /// <summary>Resolves a node selector from payload JSON: a string (var-first, else CSS/Tmpl) or a structured object.</summary>
    /// <param name="selector">The node's <c>selector</c> element.</param>
    /// <param name="ct">Cancels in-flight DOM reads during field evaluation.</param>
    public async ValueTask<ILocatorHandle> ResolveNodeAsync(JsonElement selector, CancellationToken ct)
    {
        if (selector.ValueKind == JsonValueKind.String)
        {
            // Interpolate FIRST, then apply var-first precedence: a `${…}`-built string that resolves to a bound handle
            // var wins over treating it as CSS (a literal selector renders to itself, so this is a no-op for it).
            var rendered = await CrawldadTemplate.Parse(selector.GetString()!).RenderAsync(scope, ct);
            return scope.TryResolve(rendered, out var value) && value is ILocatorHandle handle
                ? handle
                : scope.PageHandle.Locator(rendered);
        }

        if (selector.ValueKind == JsonValueKind.Object)
        {
            return ResolveMap(await EvaluateSelMapAsync(selector, ct));
        }

        throw new InterpreterException(InterpreterErrorCodes.MalformedNode, "a selector must be a string or an object");
    }

    private async ValueTask<Dictionary<string, object?>> EvaluateSelMapAsync(JsonElement selector, CancellationToken ct)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in selector.EnumerateObject())
        {
            map[property.Name] = await EvaluateSelFieldAsync(property.Name, property.Value, ct);
        }

        return map;
    }

    private async ValueTask<object?> EvaluateSelFieldAsync(string name, JsonElement value, CancellationToken ct)
    {
        switch (name)
        {
            case "css":
            case "title":
            case "in":
                return await CrawldadTemplate.Parse(value.GetString()!).RenderAsync(scope, ct);
            case "base":
                return value.GetString();
            case "nth":
                return await CrawldadExpression.Parse(value.GetString()!).EvaluateAsync(scope, ct);
            case "first":
                return value.GetBoolean();
            case "filter":
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["hasTextRegex"] = await CrawldadTemplate.Parse(value.GetProperty("hasTextRegex").GetString()!).RenderAsync(scope, ct),
                };
            default:
                throw new InterpreterException(InterpreterErrorCodes.MalformedNode, $"unknown selector key '{name}'");
        }
    }

    private static InterpreterException NotSupported(string message) =>
        new(InterpreterErrorCodes.NotSupportedInV0, message);
}
