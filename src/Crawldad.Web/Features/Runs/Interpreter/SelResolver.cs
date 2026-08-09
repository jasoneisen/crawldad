using System.Text.Json;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Crawldad.Web.Infrastructure.Browser;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// Resolves selectors to lazy <see cref="ILocatorHandle"/>s (§5.2). One resolver serves three shapes: DOM-access
/// targets from expressions (a CSS string, an opaque handle, or a structured <c>Sel</c> map), and node selectors from
/// the payload JSON (a string with <b>var-first</b> precedence — a bound handle var wins over CSS — or a structured
/// object). Structured resolution roots at exactly one of <c>css</c>/<c>xpath</c>/<c>text</c>/<c>role</c>/<c>title</c>/
/// <c>base</c> (§5.2) — <c>role</c> taking an optional accessible-name <c>name</c>, <c>base</c> optionally pairing with a
/// relative <c>css</c> — then chains the refinements <c>nth</c>/<c>first</c>/<c>filter.hasTextRegex</c>. The
/// Locator-string roots (<c>css</c>, <c>xpath</c>) resolve inside a bound frame; the <c>GetBy*</c> roots (<c>role</c>,
/// <c>text</c>, <c>title</c>) are page-level. A frame (<c>in</c>, §5.2) roots css/xpath resolution inside a bound
/// <see cref="IFrameHandle"/> instead of the page — supplied either by the enclosing node
/// (<see cref="ResolveNodeAsync"/>'s <c>frame</c> argument) or by the <c>Sel</c> map's own <c>in</c> key. The one-root
/// combination rule is enforced at save time (schema + semantic walker); a malformed structured selector reaching
/// execution surfaces as a terminal failure.
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
    /// <param name="ambientFrame">The frame supplied by the enclosing node (<c>in</c> on the action), used when the map
    /// carries no <c>in</c> of its own; null roots at the page.</param>
    public ILocatorHandle ResolveMap(Dictionary<string, object?> map, IFrameHandle? ambientFrame = null)
    {
        // The map's own `in` (a frame var name) wins over the ambient node-level frame; absent both, resolution roots at
        // the page.
        var frame = map.TryGetValue("in", out var inVar) ? RequireFrame((string)inVar!) : ambientFrame;
        var handle = ResolveRoot(map, frame);

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

    private ILocatorHandle ResolveRoot(Dictionary<string, object?> map, IFrameHandle? frame)
    {
        if (map.TryGetValue("base", out var baseVar))
        {
            // A `base` handle already carries its own frame (or page) context; the relative CSS narrows it as-is.
            var baseHandle = RequireHandle((string)baseVar!);
            return map.TryGetValue("css", out var relCss) ? baseHandle.Locator((string)relCss!) : baseHandle;
        }

        if (map.TryGetValue("css", out var css))
        {
            return RootCss(frame, (string)css!);
        }

        if (map.TryGetValue("xpath", out var xpath))
        {
            // xpath is a Locator-string engine (Playwright's "xpath=" prefix), so it roots inside a frame exactly as css
            // does — one code path (RootCss → page/frame Locator) serves both the string and structured xpath forms.
            return RootCss(frame, "xpath=" + (string)xpath!);
        }

        if (map.TryGetValue("text", out var text))
        {
            return scope.PageHandle.GetByText((string)text!); // a page-level root (frames expose a Locator-string engine only)
        }

        if (map.TryGetValue("role", out var role))
        {
            var name = map.TryGetValue("name", out var nameValue) ? (string?)nameValue : null;
            return scope.PageHandle.GetByRole((string)role!, name); // page-level, like title/text
        }

        if (map.TryGetValue("title", out var title))
        {
            return scope.PageHandle.GetByTitle((string)title!); // title is a page-level root (frames expose css/xpath only)
        }

        throw new InterpreterException(
            InterpreterErrorCodes.MalformedNode, "a Sel object needs one of 'css', 'xpath', 'text', 'role', 'title', or 'base'");
    }

    // Roots a CSS selector at the page (no frame) or inside a bound frame handle (§5.2 `in`).
    private ILocatorHandle RootCss(IFrameHandle? frame, string css) =>
        frame is null ? scope.PageHandle.Locator(css) : frame.Locator(css);

    /// <summary>Resolves a variable name that must hold a bound locator handle (<c>locate</c> <c>from</c>/<c>base</c>).</summary>
    /// <param name="name">The variable name.</param>
    /// <exception cref="InterpreterException">When the name is unbound or not a handle.</exception>
    internal ILocatorHandle RequireHandle(string name) =>
        scope.TryResolve(name, out var value) && value is ILocatorHandle handle
            ? handle
            : throw new InterpreterException(InterpreterErrorCodes.MalformedNode, $"'{name}' is not a bound locator handle");

    /// <summary>Resolves a variable name that must hold a frame handle bound by the <c>frame</c> node (§5.2 <c>in</c>).</summary>
    /// <param name="name">The variable name.</param>
    /// <exception cref="InterpreterException">When the name is unbound or not a frame handle — a terminal
    /// <c>malformed_node</c>, consistent with <see cref="RequireHandle"/>.</exception>
    internal IFrameHandle RequireFrame(string name) =>
        scope.TryResolve(name, out var value) && value is IFrameHandle frame
            ? frame
            : throw new InterpreterException(InterpreterErrorCodes.MalformedNode, $"'{name}' is not a bound frame handle");

    /// <summary>Resolves a node selector from payload JSON: a string (var-first, else CSS/Tmpl) or a structured object.</summary>
    /// <param name="selector">The node's <c>selector</c> element.</param>
    /// <param name="frame">The frame named by the node's <c>in</c> (§5.2), or null to root at the page.</param>
    /// <param name="ct">Cancels in-flight DOM reads during field evaluation.</param>
    public async ValueTask<ILocatorHandle> ResolveNodeAsync(JsonElement selector, IFrameHandle? frame, CancellationToken ct)
    {
        if (selector.ValueKind == JsonValueKind.String)
        {
            // Interpolate FIRST. At the page, apply var-first precedence: a `${…}`-built string that resolves to a bound
            // handle var wins over treating it as CSS (a literal selector renders to itself, so this is a no-op for it).
            // Inside a frame, the rendered string is CSS rooted in the frame (a frame-bound handle already knows its
            // frame, so it is passed by var name to a frame-free node, not re-rooted here).
            var rendered = await CrawldadTemplate.Parse(selector.GetString()!).RenderAsync(scope, ct);
            if (frame is not null)
            {
                return frame.Locator(rendered);
            }

            return scope.TryResolve(rendered, out var value) && value is ILocatorHandle handle
                ? handle
                : scope.PageHandle.Locator(rendered);
        }

        if (selector.ValueKind == JsonValueKind.Object)
        {
            return ResolveMap(await EvaluateSelMapAsync(selector, ct), frame);
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
            case "xpath":
            case "text":
            case "role":
            case "name":
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
}
