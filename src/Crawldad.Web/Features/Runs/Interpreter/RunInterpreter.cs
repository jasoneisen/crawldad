using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>Control-flow signal bubbling out of a block (§6 <c>break</c>/<c>continue</c>), consumed by the nearest loop.</summary>
internal enum Flow
{
    /// <summary>Fell through normally.</summary>
    Normal,

    /// <summary>A <c>break</c> fired — the enclosing loop stops.</summary>
    Break,

    /// <summary>A <c>continue</c> fired — the enclosing loop advances to the next iteration.</summary>
    Continue,
}

/// <summary>
/// Interpreter v0 (§ Deliverable 3): executes one payload against a backend and shapes its <c>result</c>. Node dispatch
/// is by the single recognised head key (unknown ⇒ terminal <c>unknown_node</c>); <c>comment</c> is a no-op. One
/// attempt only — no retry loop in P1, so timeouts/crashes surface as <c>retryable-exhausted</c> (§8.3). Config's
/// launch/context/route/retry blocks are accepted but not acted on (P1 simplification).
/// </summary>
internal sealed class RunInterpreter
{
    private readonly JsonElement _payload;
    private readonly RunScope _scope;
    private readonly IBrowserBackendRegistry _registry;
    private readonly TimeProvider _clock;

    private int _steps;
    private int _requests;
    private int _defaultTimeoutMs = 120000;
    private int _currentStepIndex;
    private string _currentKind = "config";

    public RunInterpreter(JsonElement payload, IReadOnlyDictionary<string, object?> input, IBrowserBackendRegistry registry, TimeProvider clock)
    {
        _payload = payload;
        _scope = new RunScope(input);
        _registry = registry;
        _clock = clock;
    }

    /// <summary>Runs the payload to a success or a typed failure (never throws for a modelled failure).</summary>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The run outcome (result or failure + stats).</returns>
    public async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();
        RunOutcome succeeded;
        try
        {
            var binding = await ResolveBackendAsync(ct);
            if (!_registry.TryResolve(binding.Adapter, out var backend))
            {
                throw new InterpreterException(InterpreterErrorCodes.UnknownBackendAdapter, $"no backend is registered for adapter '{binding.Adapter}'");
            }

            await using var session = await backend.ConnectAsync(binding, ct);
            _scope.Bind(await session.NewPageAsync(ct));

            _defaultTimeoutMs = ReadDefaultTimeout();
            await EvaluateVarsAsync(ct);
            await ExecuteStepsAsync(ct);

            // Assign (don't return) inside the try so result serialisation still catches handle_in_result, while the
            // success path falls through to the return below (keeping the whole method exercised).
            var result = JsonValues.ToJson(await EvaluateResultAsync(ct));
            succeeded = new RunOutcome(RunStatus.Succeeded, result, null, Stats(startedAt));
        }
        catch (InterpreterException ex)
        {
            return Failed("terminal", ex.Code, ex.Message, startedAt);
        }
        catch (ExpressionEvaluationException ex)
        {
            return Failed("terminal", ex.Code, ex.Message, startedAt);
        }
        catch (ExpressionParseException ex)
        {
            return Failed("terminal", ex.Code, ex.Message, startedAt);
        }
        catch (BrowserTimeoutException ex)
        {
            // One attempt in P1 ⇒ a retryable condition is already exhausted (§8.3); the retry loop is Phase 2.
            return Failed("retryable-exhausted", "timeout", ex.Message, startedAt);
        }
        catch (FakeBackendException ex)
        {
            return Failed("terminal", "backend_unavailable", ex.Message, startedAt);
        }

        return succeeded;
    }

    // ----- setup phases ------------------------------------------------------

    private async ValueTask<BackendBinding> ResolveBackendAsync(CancellationToken ct)
    {
        var expr = _payload.GetProperty("config").GetProperty("backend").GetString()!;
        if (await CrawldadExpression.Parse(expr).EvaluateAsync(_scope, ct) is not Dictionary<string, object?> map)
        {
            throw new InterpreterException(InterpreterErrorCodes.InvalidBackendBinding, "config.backend must resolve to a { adapter, options } object");
        }

        var adapter = map.GetValueOrDefault("adapter") as string
            ?? throw new InterpreterException(InterpreterErrorCodes.InvalidBackendBinding, "config.backend.adapter is required");
        var options = map.GetValueOrDefault("options") as IReadOnlyDictionary<string, object?>;
        var credentialRef = map.GetValueOrDefault("credentialRef") as string;
        return new BackendBinding(adapter, credentialRef, options);
    }

    private int ReadDefaultTimeout() =>
        _payload.GetProperty("config").TryGetProperty("defaultTimeoutMs", out var t) ? t.GetInt32() : 120000;

    private async ValueTask EvaluateVarsAsync(CancellationToken ct)
    {
        if (!_payload.TryGetProperty("vars", out var vars))
        {
            return;
        }

        foreach (var declared in vars.EnumerateObject())
        {
            _scope.Set(declared.Name, await EvaluateVarValueAsync(declared.Value, ct));
        }
    }

    // A string var value is an expression (§4); a non-string is a JSON literal (the fragment's `pageResults: []`).
    private async ValueTask<object?> EvaluateVarValueAsync(JsonElement value, CancellationToken ct) =>
        value.ValueKind == JsonValueKind.String
            ? await CrawldadExpression.Parse(value.GetString()!).EvaluateAsync(_scope, ct)
            : JsonValues.FromJson(value);

    private async ValueTask ExecuteStepsAsync(CancellationToken ct)
    {
        var index = 0;
        foreach (var step in _payload.GetProperty("steps").EnumerateArray())
        {
            _currentStepIndex = index;
            await ExecuteNodeAsync(step, ct); // top-level break/continue (outside any loop) is a no-op
            index++;
        }
    }

    private ValueTask<object?> EvaluateResultAsync(CancellationToken ct) =>
        CrawldadExpression.Parse(_payload.GetProperty("result").GetString()!).EvaluateAsync(_scope, ct);

    // ----- node dispatch -----------------------------------------------------

    private async ValueTask<Flow> ExecuteBlockAsync(JsonElement block, CancellationToken ct)
    {
        foreach (var node in block.EnumerateArray())
        {
            var flow = await ExecuteNodeAsync(node, ct);
            if (flow != Flow.Normal)
            {
                return flow;
            }
        }

        return Flow.Normal;
    }

    private async ValueTask<Flow> ExecuteNodeAsync(JsonElement node, CancellationToken ct)
    {
        _steps++;
        var head = node.EnumerateObject().First();
        var body = head.Value;
        _currentKind = head.Name;

        switch (head.Name)
        {
            case "comment": return Flow.Normal;
            case "goto": await GotoAsync(body, ct); return Flow.Normal;
            case "waitForLoadState": await WaitForLoadStateAsync(body, ct); return Flow.Normal;
            case "waitForRequest": await WaitForRequestAsync(body, ct); return Flow.Normal;
            case "waitFor": await WaitForAsync(body, ct); return Flow.Normal;
            case "click": await ClickAsync(body, ct); return Flow.Normal;
            case "fill": await FillAsync(body, ct); return Flow.Normal;
            case "clear": await ClearAsync(body, ct); return Flow.Normal;
            case "locate": await LocateAsync(body, ct); return Flow.Normal;
            case "set": await SetAsync(body, ct); return Flow.Normal;
            case "push": await PushAsync(body, ct); return Flow.Normal;
            case "if": return await IfAsync(body, ct);
            case "loop": return await LoopAsync(body, ct);
            case "forEach": return await ForEachAsync(body, ct);
            case "break": return await SignalAsync(body, Flow.Break, ct);
            case "continue": return await SignalAsync(body, Flow.Continue, ct);
            default: throw new InterpreterException(InterpreterErrorCodes.UnknownNode, $"unknown node '{head.Name}'");
        }
    }

    // ----- actions -----------------------------------------------------------

    private async ValueTask GotoAsync(JsonElement body, CancellationToken ct)
    {
        var url = await CrawldadTemplate.Parse(body.GetProperty("url").GetString()!).RenderAsync(_scope, ct);
        await _scope.PageHandle.GotoAsync(url, OptString(body, "waitUntil"), Timeout(body), ct);
        _requests++;
    }

    private ValueTask WaitForLoadStateAsync(JsonElement body, CancellationToken ct) =>
        new(_scope.PageHandle.WaitForLoadStateAsync(body.GetProperty("state").GetString()!, Timeout(body), ct));

    private async ValueTask WaitForRequestAsync(JsonElement body, CancellationToken ct)
    {
        var urlPrefix = await CrawldadTemplate.Parse(body.GetProperty("urlPrefix").GetString()!).RenderAsync(_scope, ct);
        var trigger = body.GetProperty("trigger");
        await _scope.PageHandle.RunAndWaitForRequestAsync(
            () => ExecuteBlockAsync(trigger, ct).AsTask(),
            urlPrefix,
            OptString(body, "method"),
            Timeout(body),
            ct);
        _requests++;
    }

    private async ValueTask WaitForAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        await handle.WaitForAsync(body.GetProperty("state").GetString()!, Timeout(body), ct);
    }

    private async ValueTask ClickAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        await handle.ClickAsync(Timeout(body), ct);
    }

    private async ValueTask FillAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        var value = ExpressionValues.ToStringValue(await ExprAsync(body.GetProperty("value"), ct));
        await handle.FillAsync(value, ct);
    }

    private async ValueTask ClearAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        await handle.ClearAsync(ct);
    }

    // ----- locate (both forms) ----------------------------------------------

    private async ValueTask LocateAsync(JsonElement body, CancellationToken ct)
    {
        if (body.TryGetProperty("in", out _))
        {
            throw new InterpreterException(InterpreterErrorCodes.NotSupportedInV0, "frames ('in') are not supported in v0");
        }

        var handle = body.TryGetProperty("from", out var from)
            ? await LocateFromHandleAsync(body, from.GetString()!, ct)
            : await LocateFromSelectorAsync(body, ct);

        _scope.Set(body.GetProperty("var").GetString()!, handle);
    }

    private async ValueTask<ILocatorHandle> LocateFromHandleAsync(JsonElement body, string fromVar, CancellationToken ct)
    {
        var handle = _scope.Sel.RequireHandle(fromVar);

        if (body.TryGetProperty("filter", out var filter))
        {
            handle = handle.Filter(await CrawldadTemplate.Parse(filter.GetProperty("hasTextRegex").GetString()!).RenderAsync(_scope, ct));
        }

        if (body.TryGetProperty("nth", out var nth))
        {
            handle = handle.Nth((int)(long)(await ExprAsync(nth, ct))!);
        }

        if (body.TryGetProperty("first", out var first) && first.GetBoolean())
        {
            handle = handle.First;
        }

        return handle;
    }

    private async ValueTask<ILocatorHandle> LocateFromSelectorAsync(JsonElement body, CancellationToken ct)
    {
        // `base` names a parent handle; the selector is then a relative CSS template on it.
        if (body.TryGetProperty("base", out var baseVar))
        {
            var css = await CrawldadTemplate.Parse(body.GetProperty("selector").GetString()!).RenderAsync(_scope, ct);
            return _scope.Sel.RequireHandle(baseVar.GetString()!).Locator(css);
        }

        return await _scope.Sel.ResolveNodeAsync(body.GetProperty("selector"), ct);
    }

    // ----- state + control flow ---------------------------------------------

    private async ValueTask SetAsync(JsonElement body, CancellationToken ct)
    {
        if (body.TryGetProperty("path", out _))
        {
            throw new InterpreterException(InterpreterErrorCodes.NotSupportedInV0, "computed-key 'set' paths are Phase 2");
        }

        _scope.Set(body.GetProperty("var").GetString()!, await ExprAsync(body.GetProperty("value"), ct));
    }

    private async ValueTask PushAsync(JsonElement body, CancellationToken ct) =>
        _scope.Push(body.GetProperty("into").GetString()!, await ExprAsync(body.GetProperty("value"), ct));

    private async ValueTask<Flow> IfAsync(JsonElement body, CancellationToken ct)
    {
        if (ExpressionValues.RequireBool(await ExprAsync(body.GetProperty("cond"), ct)))
        {
            return await ExecuteBlockAsync(body.GetProperty("then"), ct);
        }

        return body.TryGetProperty("else", out var elseBlock)
            ? await ExecuteBlockAsync(elseBlock, ct)
            : Flow.Normal;
    }

    private async ValueTask<Flow> LoopAsync(JsonElement body, CancellationToken ct)
    {
        var max = RequireMaxIterations(body);
        var forSpec = body.GetProperty("for");
        var varName = forSpec.GetProperty("var").GetString()!;
        var toExpr = forSpec.GetProperty("to");
        var inclusive = forSpec.TryGetProperty("inclusiveTo", out var inc) && inc.GetBoolean();
        var step = forSpec.TryGetProperty("step", out var s) ? (long)(await ExprAsync(s, ct))! : 1L;
        var doBlock = body.GetProperty("do");

        var i = (long)(await ExprAsync(forSpec.GetProperty("from"), ct))!;
        var iterations = 0L;
        using var shadow = _scope.Shadow((varName, (object?)i));

        while (true)
        {
            _scope.Set(varName, i);
            var to = (long)(await ExprAsync(toExpr, ct))!; // re-evaluated each iteration, matching the reference's condition
            if (inclusive ? i > to : i >= to)
            {
                break;
            }

            if (++iterations > max)
            {
                throw MaxIterationsExceeded();
            }

            if (await ExecuteBlockAsync(doBlock, ct) == Flow.Break)
            {
                break;
            }

            i += step;
        }

        return Flow.Normal;
    }

    private async ValueTask<Flow> ForEachAsync(JsonElement body, CancellationToken ct)
    {
        var max = RequireMaxIterations(body);
        var asName = body.GetProperty("as").GetString()!;
        var indexName = body.TryGetProperty("index", out var idx) ? idx.GetString() : null;
        var doBlock = body.GetProperty("do");
        var source = await ExprAsync(body.GetProperty("in"), ct);

        if (source is List<object?> list)
        {
            return await IterateAsync(list.Count, i => list[i], asName, indexName, max, doBlock, ct);
        }

        if (source is ILocatorHandle handle)
        {
            var count = await handle.CountAsync(ct);
            return await IterateAsync(count, handle.Nth, asName, indexName, max, doBlock, ct);
        }

        throw new InterpreterException(InterpreterErrorCodes.MalformedNode, "forEach 'in' must be an array or a bound locator");
    }

    private async ValueTask<Flow> IterateAsync(
        int count, Func<int, object?> itemAt, string asName, string? indexName, long max, JsonElement doBlock, CancellationToken ct)
    {
        using var shadow = indexName is null
            ? _scope.Shadow((asName, null))
            : _scope.Shadow((asName, null), (indexName, null));

        var iterations = 0L;
        for (var i = 0; i < count; i++)
        {
            if (++iterations > max)
            {
                throw MaxIterationsExceeded();
            }

            _scope.Set(asName, itemAt(i));
            if (indexName is not null)
            {
                _scope.Set(indexName, (long)i);
            }

            if (await ExecuteBlockAsync(doBlock, ct) == Flow.Break)
            {
                break;
            }
        }

        return Flow.Normal;
    }

    private async ValueTask<Flow> SignalAsync(JsonElement body, Flow signal, CancellationToken ct)
    {
        if (!body.TryGetProperty("when", out var when) || ExpressionValues.RequireBool(await ExprAsync(when, ct)))
        {
            return signal;
        }

        return Flow.Normal;
    }

    // ----- helpers -----------------------------------------------------------

    private async ValueTask<ILocatorHandle> ResolveSelectorAsync(JsonElement body, CancellationToken ct)
    {
        if (body.TryGetProperty("in", out _))
        {
            throw new InterpreterException(InterpreterErrorCodes.NotSupportedInV0, "frames ('in') are not supported in v0");
        }

        return await _scope.Sel.ResolveNodeAsync(body.GetProperty("selector"), ct);
    }

    private ValueTask<object?> ExprAsync(JsonElement expr, CancellationToken ct) =>
        CrawldadExpression.Parse(expr.GetString()!).EvaluateAsync(_scope, ct);

    private long RequireMaxIterations(JsonElement body) =>
        body.TryGetProperty("maxIterations", out var max)
            ? max.GetInt64()
            : throw new InterpreterException(InterpreterErrorCodes.MissingMaxIterations, $"'{_currentKind}' requires a maxIterations cap (§6)");

    private static InterpreterException MaxIterationsExceeded() =>
        new(InterpreterErrorCodes.MaxIterationsExceeded, "loop exceeded its maxIterations cap");

    private static string? OptString(JsonElement body, string field) =>
        body.TryGetProperty(field, out var value) ? value.GetString() : null;

    private int Timeout(JsonElement body) =>
        body.TryGetProperty("timeoutMs", out var t) ? t.GetInt32() : _defaultTimeoutMs;

    private RunStats Stats(DateTimeOffset startedAt) =>
        new((long)(_clock.GetUtcNow() - startedAt).TotalMilliseconds, _steps, _requests, 0, 0);

    private RunOutcome Failed(string failureClass, string code, string message, DateTimeOffset startedAt) =>
        new(RunStatus.Failed, null, new RunFailureDetail(failureClass, code, message, new RunStepRef(_currentStepIndex, _currentKind)), Stats(startedAt));
}
