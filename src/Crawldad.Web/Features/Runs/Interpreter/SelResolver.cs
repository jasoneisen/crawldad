using System.Text.Json;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Crawldad.Web.Infrastructure.Browser;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>Resolves selectors to lazy <see cref="ILocatorHandle"/>s. Serves DOM-access targets from expressions (CSS
/// string, handle, or structured <c>Sel</c> map) and node selectors from payload JSON (string, with var-first
/// precedence over CSS, or structured object). <c>css</c>/<c>xpath</c> root inside a bound frame; <c>role</c>/<c>text</c>/<c>title</c> are page-level only.</summary>
internal sealed class SelResolver(RunScope scope)
{
    /// <summary>Resolves a DOM-access target (CSS string | handle | structured map) and applies an optional relative CSS.</summary>
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

        // An opaque handle here might be an IFrameHandle (bound by the `frame` node) rather than a locator — a var
        // reference carries no type gate, so a frame var in a target position (e.g. exists(fr)) flows straight here.
        // Only an ILocatorHandle is a valid DOM-read target; a frame handle is a classified type_error, never a raw unbox.
        return target switch
        {
            ILocatorHandle handle => handle,
            IFrameHandle => throw ExpressionValues.TypeError(
                "a frame handle is not a DOM read target — root a selector inside it with 'in', don't pass the frame itself"),
            _ => throw ExpressionValues.TypeError(
                $"DOM target must be a css string, a locator handle, or a Sel map, got {ExpressionValues.TypeName(target)}"),
        };
    }

    /// <summary>Resolves a structured <c>Sel</c> map (values already evaluated) by chaining seam refinements.
    /// <paramref name="ambientFrame"/> is used only when the map carries no <c>in</c> of its own; null roots at the page.</summary>
    public ILocatorHandle ResolveMap(Dictionary<string, object?> map, IFrameHandle? ambientFrame = null)
    {
        // The map's own `in` (a frame var name) wins over the ambient node-level frame; absent both, resolution roots
        // at the page. Every field below is read UNCOERCED on the expression path, so each is classified through an
        // ExpressionValues.Require* check (terminal type_error) rather than a raw unbox; the node path pre-coerces fields.
        var frame = map.TryGetValue("in", out var inVar) ? RequireFrame(ExpressionValues.RequireString(inVar, "selector 'in'")) : ambientFrame;
        var handle = ResolveRoot(map, frame);

        if (map.TryGetValue("filter", out var filter))
        {
            var filterMap = filter as Dictionary<string, object?>
                ?? throw ExpressionValues.TypeError($"selector 'filter' must be an object, got {ExpressionValues.TypeName(filter)}");
            handle = handle.Filter(filterMap.TryGetValue("hasTextRegex", out var hasTextRegex)
                ? ExpressionValues.RequireString(hasTextRegex, "filter 'hasTextRegex'")
                : throw ExpressionValues.TypeError("selector 'filter' requires a 'hasTextRegex' string"));
        }

        if (map.TryGetValue("nth", out var nth))
        {
            // The sibling of the locate.from nth cast — a structured Sel nth is an already-evaluated Expr result, so
            // it classifies through the same RequireNthIndex (terminal type_error / index_out_of_range), never a raw unbox.
            handle = handle.Nth(ExpressionValues.RequireNthIndex(nth));
        }

        // `first` classifies through RequireFirstFlag (terminal type_error), the sibling of the nth cast above. The
        // node path feeds a schema-checked JSON bool, but the expression path feeds an UNCOERCED Expr value, so a
        // non-bool first (e.g. exists({ css:'tr', first:'x' })) is a classified failure, not a raw unbox.
        if (map.TryGetValue("first", out var first) && ExpressionValues.RequireFirstFlag(first))
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
            var baseHandle = RequireHandle(ExpressionValues.RequireString(baseVar, "selector 'base'"));
            return map.TryGetValue("css", out var relCss) ? baseHandle.Locator(ExpressionValues.RequireString(relCss, "selector 'css'")) : baseHandle;
        }

        if (map.TryGetValue("css", out var css))
        {
            return RootCss(frame, ExpressionValues.RequireString(css, "selector 'css'"));
        }

        if (map.TryGetValue("xpath", out var xpath))
        {
            // xpath is a Locator-string engine (Playwright's "xpath=" prefix), so it roots inside a frame exactly as css
            // does — one code path (RootCss → page/frame Locator) serves both the string and structured xpath forms.
            return RootCss(frame, "xpath=" + ExpressionValues.RequireString(xpath, "selector 'xpath'"));
        }

        if (map.TryGetValue("text", out var text))
        {
            return scope.PageHandle.GetByText(ExpressionValues.RequireString(text, "selector 'text'")); // a page-level root (frames expose a Locator-string engine only)
        }

        if (map.TryGetValue("role", out var role))
        {
            var name = map.TryGetValue("name", out var nameValue) ? ExpressionValues.RequireString(nameValue, "selector 'name'") : null;
            return scope.PageHandle.GetByRole(ExpressionValues.RequireString(role, "selector 'role'"), name); // page-level, like title/text
        }

        if (map.TryGetValue("title", out var title))
        {
            return scope.PageHandle.GetByTitle(ExpressionValues.RequireString(title, "selector 'title'")); // title is a page-level root (frames expose css/xpath only)
        }

        throw new InterpreterException(
            InterpreterErrorCodes.MalformedNode, "a Sel object needs one of 'css', 'xpath', 'text', 'role', 'title', or 'base'");
    }

    // Roots a CSS selector at the page (no frame) or inside a bound frame handle (the `in` key).
    private ILocatorHandle RootCss(IFrameHandle? frame, string css) =>
        frame is null ? scope.PageHandle.Locator(css) : frame.Locator(css);

    /// <summary>Resolves a variable name that must hold a bound locator handle (<c>locate</c> <c>from</c>/<c>base</c>).</summary>
    internal ILocatorHandle RequireHandle(string name) =>
        scope.TryResolve(name, out var value) && value is ILocatorHandle handle
            ? handle
            : throw new InterpreterException(InterpreterErrorCodes.MalformedNode, $"'{name}' is not a bound locator handle");

    /// <summary>Resolves a variable name that must hold a frame handle bound by the <c>frame</c> node (the <c>in</c> key).</summary>
    internal IFrameHandle RequireFrame(string name) =>
        scope.TryResolve(name, out var value) && value is IFrameHandle frame
            ? frame
            : throw new InterpreterException(InterpreterErrorCodes.MalformedNode, $"'{name}' is not a bound frame handle");

    /// <summary>Resolves a node selector from payload JSON: a string (var-first, else CSS/Tmpl) or a structured object.</summary>
    public async ValueTask<ILocatorHandle> ResolveNodeAsync(JsonElement selector, IFrameHandle? frame, CancellationToken ct)
    {
        if (selector.ValueKind == JsonValueKind.String)
        {
            // Interpolate FIRST, then apply var-first precedence: a rendered string that resolves to a bound handle var
            // wins over treating it as CSS (a no-op for a literal selector). Inside a frame, the rendered string is CSS
            // rooted in the frame (a frame-bound handle is passed by var name to a frame-free node, not re-rooted here).
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
