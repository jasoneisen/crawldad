using System.Security.Cryptography;
using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Crawldad.Web.Infrastructure.Storage;

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
/// The interpreter (§ Deliverables): executes one payload against a backend and shapes its <c>result</c>. A parse
/// pre-pass (§ Deliverable 4) rejects unknown head keys and missing <c>maxIterations</c> before any side effect; node
/// dispatch is by the single recognised head key through a shared table so validation and execution agree by
/// construction (<c>comment</c> is a no-op). The whole program is wrapped in the retry/resilience layer (§8.3): a
/// retryable failure (<c>timeout</c>/<c>pageCrashed</c>, or a retryable <c>fail</c>) re-runs the program with a fresh
/// run scope on the same session — reopening and rebinding the page on a crash (§3.6) — until it succeeds, exhausts, or
/// hits a terminal failure. Config's launch/context/route blocks are accepted but not acted on (later phases).
/// </summary>
internal sealed class RunInterpreter
{
    private static readonly IReadOnlySet<string> _defaultRetryOn =
        new HashSet<string>(StringComparer.Ordinal) { "timeout", "pageCrashed" };

    private readonly JsonElement _payload;
    private readonly IReadOnlyDictionary<string, object?> _input;
    private readonly IBrowserBackendRegistry _registry;
    private readonly IDownloadSinkRegistry _sinks;
    private readonly TimeProvider _clock;
    private readonly List<object> _events = [];
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<Flow>>> _dispatch;

    private RunScope _scope;
    private IPageHandle _page = null!;
    private int _steps;
    private int _requests;
    private int _downloads;
    private int _defaultTimeoutMs = 120000;
    private int _currentStepIndex;
    private string _currentKind = "config";

    public RunInterpreter(
        JsonElement payload,
        IReadOnlyDictionary<string, object?> input,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        TimeProvider clock)
    {
        _payload = payload;
        _input = input;
        _registry = registry;
        _sinks = sinks;
        _clock = clock;
        _scope = new RunScope(input); // an input-only scope for backend resolution; execution rebuilds it per attempt
        _dispatch = BuildDispatch();
    }

    /// <summary>Runs the payload to a success or a typed failure (never throws for a modelled failure).</summary>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The run outcome (result or failure + stats + trace events).</returns>
    public async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();
        RunOutcome outcome;
        try
        {
            ValidateProgram(); // §Deliverable 4: reject unknown head keys / missing maxIterations before any side effect
            var policy = ParseRetryPolicy();

            var binding = await ResolveBackendAsync(ct);
            if (!_registry.TryResolve(binding.Adapter, out var backend))
            {
                throw new InterpreterException(InterpreterErrorCodes.UnknownBackendAdapter, $"no backend is registered for adapter '{binding.Adapter}'");
            }

            await using var session = await backend.ConnectAsync(binding, ct);
            _page = await session.NewPageAsync(ct);
            _defaultTimeoutMs = ReadDefaultTimeout();

            // Assign (don't return) inside the try so the setup catches below still apply, while the happy path falls
            // through to the return below — keeping the try's fall-through exercised.
            outcome = await ExecuteWithRetryAsync(session, policy, startedAt, ct);
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
        catch (FakeBackendException ex)
        {
            return Failed("terminal", "backend_unavailable", ex.Message, startedAt);
        }

        return outcome;
    }

    // ----- retry/resilience layer (§8.3) -------------------------------------

    private async Task<RunOutcome> ExecuteWithRetryAsync(IBrowserSession session, RetryPolicy policy, DateTimeOffset startedAt, CancellationToken ct)
    {
        var exhaustedCode = "";
        var exhaustedMessage = "";
        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            try
            {
                _scope = new RunScope(_input); // FRESH scope per attempt — re-evaluate vars, same session
                _scope.Bind(_page);
                await EvaluateVarsAsync(ct);
                await ExecuteStepsAsync(ct);

                var result = JsonValues.ToJson(await EvaluateResultAsync(ct));
                return new RunOutcome(RunStatus.Succeeded, result, null, Stats(startedAt), _events);
            }
            catch (Exception ex) when (ex is BrowserException or CrawldadFailureException or InterpreterException or ExpressionEvaluationException or ExpressionParseException)
            {
                var (code, isRetryableClass, eligibleForRetry) = Classify(ex, policy);
                if (!eligibleForRetry)
                {
                    return Failed(isRetryableClass ? "retryable-exhausted" : "terminal", code, ex.Message, startedAt);
                }

                // Retryable and permitted: record the attempt, reopen the page on a crash (§3.6), delay, then let the
                // loop re-run the whole program. The last attempt falls through to the exhaustion return below.
                exhaustedCode = code;
                exhaustedMessage = ex.Message;
                if (attempt < policy.MaxAttempts)
                {
                    _events.Add(new RunAttemptFailed(attempt, code, _clock.GetUtcNow()));
                    if (ex is BrowserPageCrashedException)
                    {
                        await ReopenPageAsync(session, ct); // reopen on the SAME context and rebind
                    }

                    if (policy.DelayMs > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(policy.DelayMs), _clock, ct);
                    }
                }
            }
        }

        return Failed("retryable-exhausted", exhaustedCode, exhaustedMessage, startedAt);
    }

    // isRetryableClass: the failure is retryable-class (→ "retryable-exhausted" if not retried). eligibleForRetry:
    // policy permits retrying it (a browser condition listed in retryOn, or any retryable `fail`).
    private static (string Code, bool IsRetryableClass, bool EligibleForRetry) Classify(Exception ex, RetryPolicy policy) => ex switch
    {
        BrowserTimeoutException => ("timeout", true, policy.RetryOn.Contains("timeout")),
        BrowserPageCrashedException => ("pageCrashed", true, policy.RetryOn.Contains("pageCrashed")),
        CrawldadFailureException f => (f.Code, f.IsRetryable, f.IsRetryable),
        InterpreterException i => (i.Code, false, false),
        ExpressionEvaluationException e => (e.Code, false, false),
        _ => (((ExpressionParseException)ex).Code, false, false),
    };

    private async ValueTask ReopenPageAsync(IBrowserSession session, CancellationToken ct)
    {
        await CloseQuietlyAsync(_page, ct);
        _page = await session.NewPageAsync(ct); // SAME session/context, rebound into the next attempt's scope
    }

    /// <summary>Closes a page best-effort, tolerating a crashed page's close failure (§3.6). Internal for direct testing.</summary>
    /// <param name="page">The page to close.</param>
    /// <param name="ct">Cancels the close.</param>
    internal static async Task CloseQuietlyAsync(IPageHandle page, CancellationToken ct)
    {
        try
        {
            await page.CloseAsync(ct);
        }
        catch (BrowserException)
        {
            // Tolerate — a crashed page may fail to close; we only need a fresh one on the same context.
        }
    }

    private RetryPolicy ParseRetryPolicy()
    {
        if (!_payload.GetProperty("config").TryGetProperty("retry", out var retry))
        {
            return new RetryPolicy(1, 0, _defaultRetryOn); // absent ⇒ a single attempt (P1 behaviour unchanged)
        }

        var maxAttempts = retry.TryGetProperty("maxAttempts", out var m) ? m.GetInt32() : 1;
        var delayMs = retry.TryGetProperty("delayMs", out var d) ? d.GetInt32() : 0;
        var retryOn = retry.TryGetProperty("retryOn", out var r) ? ReadStringSet(r) : _defaultRetryOn;
        return new RetryPolicy(maxAttempts, delayMs, retryOn);
    }

    private static HashSet<string> ReadStringSet(JsonElement array)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            set.Add(item.GetString()!);
        }

        return set;
    }

    private sealed record RetryPolicy(int MaxAttempts, int DelayMs, IReadOnlySet<string> RetryOn);

    // ----- parse-time validation (§Deliverable 4) ----------------------------

    // The run-time structural pre-pass: the SAME PayloadValidator save-time uses (Deliverable 3 — one implementation,
    // no divergence). It rejects unknown head keys and missing maxIterations before any side effect; the first issue
    // becomes the terminal §10 failure at its step. Save-time layers the JSON Schema + semantic pass on top of this;
    // the run path deliberately keeps only this structural pre-pass, letting defined-before-use / expression-parse
    // faults surface at evaluation exactly as before (inline POST /runs payloads are not held to the save-time bar).
    private void ValidateProgram()
    {
        var issues = PayloadValidator.ValidateStructure(_payload);
        if (issues.Count == 0)
        {
            return;
        }

        var first = issues[0];
        _currentStepIndex = first.StepIndex;
        _currentKind = first.StepKind;
        throw new InterpreterException(first.Code, first.Message);
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

    private Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<Flow>>> BuildDispatch() =>
        new(StringComparer.Ordinal)
        {
            ["comment"] = static (_, _) => new ValueTask<Flow>(Flow.Normal),
            ["goto"] = Effect(GotoAsync),
            ["waitForLoadState"] = Effect(WaitForLoadStateAsync),
            ["waitForRequest"] = Effect(WaitForRequestAsync),
            ["waitFor"] = Effect(WaitForAsync),
            ["frame"] = Effect(FrameAsync),
            ["addStyleTag"] = Effect(AddStyleTagAsync),
            ["click"] = Effect(ClickAsync),
            ["fill"] = Effect(FillAsync),
            ["clear"] = Effect(ClearAsync),
            ["locate"] = Effect(LocateAsync),
            ["download"] = Effect(DownloadAsync),
            ["set"] = Effect(SetAsync),
            ["push"] = Effect(PushAsync),
            ["log"] = Effect(LogAsync),
            ["guard"] = Effect(GuardAsync),
            ["fail"] = Effect(FailAsync),
            ["if"] = IfAsync,
            ["switch"] = SwitchAsync,
            ["loop"] = LoopAsync,
            ["forEach"] = ForEachAsync,
            ["break"] = (body, ct) => SignalAsync(body, Flow.Break, ct),
            ["continue"] = (body, ct) => SignalAsync(body, Flow.Continue, ct),
        };

    // Wraps an effectful node (returns no Flow) as a dispatch entry that always falls through (Flow.Normal).
    private static Func<JsonElement, CancellationToken, ValueTask<Flow>> Effect(Func<JsonElement, CancellationToken, ValueTask> effect) =>
        async (body, ct) =>
        {
            await effect(body, ct);
            return Flow.Normal;
        };

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

    private ValueTask<Flow> ExecuteNodeAsync(JsonElement node, CancellationToken ct)
    {
        _steps++;
        var head = node.EnumerateObject().First();
        _currentKind = head.Name;
        return _dispatch[head.Name](head.Value, ct); // head.Name is a known kind — ValidateProgram guaranteed it
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

    // frame binds a FrameLocator handle to a var (§5.1); `in:` on later nodes/Sels roots resolution inside it. The
    // selector is the iframe element's CSS Tmpl (Playwright FrameLocator takes a string, so a Sel here is its string form).
    private async ValueTask FrameAsync(JsonElement body, CancellationToken ct)
    {
        var selector = await CrawldadTemplate.Parse(body.GetProperty("selector").GetString()!).RenderAsync(_scope, ct);
        _scope.Set(body.GetProperty("var").GetString()!, _scope.PageHandle.FrameLocator(selector));
    }

    // addStyleTag injects CSS (data, not code — §15 tension #3); the reference forces record tabs visible (:209).
    private async ValueTask AddStyleTagAsync(JsonElement body, CancellationToken ct)
    {
        var content = await CrawldadTemplate.Parse(body.GetProperty("content").GetString()!).RenderAsync(_scope, ct);
        await _scope.PageHandle.AddStyleTagAsync(content, ct);
    }

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

    // `state` defaults to "visible" (Playwright's Locator.WaitForAsync default) when omitted — the reference's
    // attachment page-number wait passes no state (:612-614).
    private async ValueTask WaitForAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        await handle.WaitForAsync(OptString(body, "state") ?? "visible", Timeout(body), ct);
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
        // `base` names a parent handle (which carries its own page/frame context); the selector is then a relative CSS
        // template on it.
        if (body.TryGetProperty("base", out var baseVar))
        {
            var css = await CrawldadTemplate.Parse(body.GetProperty("selector").GetString()!).RenderAsync(_scope, ct);
            return _scope.Sel.RequireHandle(baseVar.GetString()!).Locator(css);
        }

        return await _scope.Sel.ResolveNodeAsync(body.GetProperty("selector"), FrameArg(body), ct);
    }

    // ----- download (§9.3) ---------------------------------------------------

    // Runs the trigger, drains the download to compute the content identity (§9.3, = AttachmentHashing), and streams it
    // to the target sink — idempotently: an already-present blob short-circuits to stored:true WITHOUT re-uploading.
    // Binds dl = { contentId, sha256, sizeBytes, storedAs, stored }; download failure/timeout is retryable (:560).
    private async ValueTask DownloadAsync(JsonElement body, CancellationToken ct)
    {
        var sink = ResolveSink(await ExprAsync(body.GetProperty("to"), ct));
        var trigger = body.GetProperty("trigger");
        var download = await _scope.PageHandle.RunAndWaitForDownloadAsync(
            () => ExecuteBlockAsync(trigger, ct).AsTask(), Timeout(body), ct);

        byte[] data;
        await using (var content = await download.OpenReadAsync(ct))
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            data = buffer.ToArray();
        }

        var hash = SHA256.HashData(data);
        var contentId = AttachmentContentId.FromHash(hash);
        var sha256 = Convert.ToHexStringLower(hash);
        var storedAs = AttachmentContentId.BuildStoredName(contentId, download.SuggestedFilename);
        long sizeBytes = data.Length;

        // exists ⇒ stored:true, no re-upload; else the sink's own success (false ⇒ the reference's handleDownload reject).
        var stored = await sink.ExistsAsync(contentId, ct)
            || await sink.StoreAsync(new StoredDownload(contentId, storedAs, sizeBytes, sha256), new MemoryStream(data, writable: false), ct);

        _downloads++;
        _scope.Set(body.GetProperty("var").GetString()!, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contentId"] = contentId.ToString(),
            ["sha256"] = sha256,
            ["sizeBytes"] = sizeBytes,
            ["storedAs"] = storedAs,
            ["stored"] = stored,
        });
    }

    private IDownloadSink ResolveSink(object? target)
    {
        if (target is not Dictionary<string, object?> map)
        {
            throw new InterpreterException(InterpreterErrorCodes.InvalidDownloadTarget, "download 'to' must resolve to a storageTarget { kind, name } object");
        }

        var kind = map.GetValueOrDefault("kind") as string
            ?? throw new InterpreterException(InterpreterErrorCodes.InvalidDownloadTarget, "a storageTarget requires a string 'kind'");

        return _sinks.TryResolve(kind, out var sink)
            ? sink
            : throw new InterpreterException(InterpreterErrorCodes.UnknownDownloadSink, $"no download sink is registered for kind '{kind}'");
    }

    // ----- state + control flow ---------------------------------------------

    private async ValueTask SetAsync(JsonElement body, CancellationToken ct)
    {
        var value = await ExprAsync(body.GetProperty("value"), ct);
        if (body.TryGetProperty("path", out var path))
        {
            await SetPathAsync(body.GetProperty("var").GetString()!, path.GetString()!, value, ct);
            return;
        }

        _scope.Set(body.GetProperty("var").GetString()!, value);
    }

    // set with a `path` mutates INSIDE an existing map var (§7.4). The var and every intermediate segment must be a
    // map (else terminal type_error); the leaf key is upserted (add-or-overwrite — the documented B.2 micro-divergence
    // from Dictionary.Add).
    private async ValueTask SetPathAsync(string varName, string path, object? value, CancellationToken ct)
    {
        if (!_scope.TryResolve(varName, out var target) || target is not Dictionary<string, object?> cursor)
        {
            throw ExpressionValues.TypeError($"set path target '{varName}' is not a map");
        }

        var segments = SetPath.Parse(path);
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var key = await segments[i].KeyAsync(_scope, ct);
            if (!cursor.TryGetValue(key, out var next) || next is not Dictionary<string, object?> childMap)
            {
                throw ExpressionValues.TypeError($"set path cannot traverse '{key}' — it is not a map");
            }

            cursor = childMap;
        }

        cursor[await segments[^1].KeyAsync(_scope, ct)] = value;
    }

    private async ValueTask PushAsync(JsonElement body, CancellationToken ct) =>
        _scope.Push(body.GetProperty("into").GetString()!, await ExprAsync(body.GetProperty("value"), ct));

    private async ValueTask LogAsync(JsonElement body, CancellationToken ct)
    {
        var level = body.GetProperty("level").GetString()!;
        var message = await CrawldadTemplate.Parse(body.GetProperty("message").GetString()!).RenderAsync(_scope, ct);
        _events.Add(new LogEmitted(level, message, _clock.GetUtcNow()));
    }

    private async ValueTask GuardAsync(JsonElement body, CancellationToken ct)
    {
        if (ExpressionValues.RequireBool(await ExprAsync(body.GetProperty("cond"), ct)))
        {
            return; // the condition held — nothing to do
        }

        await RaiseFailureAsync(body.GetProperty("elseFail"), ct);
    }

    private ValueTask FailAsync(JsonElement body, CancellationToken ct) => RaiseFailureAsync(body, ct);

    // Builds and throws the typed failure from a §4 Failure payload, rendering its message template at raise time.
    private async ValueTask RaiseFailureAsync(JsonElement failure, CancellationToken ct)
    {
        var failureClass = failure.GetProperty("class").GetString()!;
        var code = failure.GetProperty("code").GetString()!;
        var message = await CrawldadTemplate.Parse(failure.GetProperty("message").GetString()!).RenderAsync(_scope, ct);
        throw new CrawldadFailureException(failureClass, code, message);
    }

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

    // switch: first true `when` wins; its Flow (a break/continue) propagates like `if`. No default + no match = no-op.
    private async ValueTask<Flow> SwitchAsync(JsonElement body, CancellationToken ct)
    {
        foreach (var branch in body.GetProperty("cases").EnumerateArray())
        {
            if (ExpressionValues.RequireBool(await ExprAsync(branch.GetProperty("when"), ct)))
            {
                return await ExecuteBlockAsync(branch.GetProperty("do"), ct);
            }
        }

        return body.TryGetProperty("default", out var def)
            ? await ExecuteBlockAsync(def, ct)
            : Flow.Normal;
    }

    private ValueTask<Flow> LoopAsync(JsonElement body, CancellationToken ct) =>
        body.TryGetProperty("while", out var whileExpr)
            ? WhileLoopAsync(body, whileExpr, ct)
            : ForLoopAsync(body, ct);

    private async ValueTask<Flow> ForLoopAsync(JsonElement body, CancellationToken ct)
    {
        var max = ReadMaxIterations(body);
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

            if (++iterations > max && StopAtCap(body))
            {
                break;
            }

            if (await ExecuteBlockAsync(doBlock, ct) == Flow.Break)
            {
                break;
            }

            i += step;
        }

        return Flow.Normal;
    }

    // while: do-while — BODY FIRST, then test (§6, matching the reference's do…while).
    private async ValueTask<Flow> WhileLoopAsync(JsonElement body, JsonElement whileExpr, CancellationToken ct)
    {
        var max = ReadMaxIterations(body);
        var doBlock = body.GetProperty("do");
        var iterations = 0L;

        while (true)
        {
            if (++iterations > max && StopAtCap(body))
            {
                break;
            }

            if (await ExecuteBlockAsync(doBlock, ct) == Flow.Break)
            {
                break;
            }

            if (!ExpressionValues.RequireBool(await ExprAsync(whileExpr, ct)))
            {
                break;
            }
        }

        return Flow.Normal;
    }

    private async ValueTask<Flow> ForEachAsync(JsonElement body, CancellationToken ct)
    {
        var asName = body.GetProperty("as").GetString()!;
        var indexName = body.TryGetProperty("index", out var idx) ? idx.GetString() : null;
        var doBlock = body.GetProperty("do");
        var source = await ExprAsync(body.GetProperty("in"), ct);

        if (source is List<object?> list)
        {
            return await IterateAsync(list.Count, i => list[i], asName, indexName, body, doBlock, ct);
        }

        if (source is ILocatorHandle handle)
        {
            var count = await handle.CountAsync(ct);
            return await IterateAsync(count, handle.Nth, asName, indexName, body, doBlock, ct);
        }

        throw new InterpreterException(InterpreterErrorCodes.MalformedNode, "forEach 'in' must be an array or a bound locator");
    }

    private async ValueTask<Flow> IterateAsync(
        int count, Func<int, object?> itemAt, string asName, string? indexName, JsonElement body, JsonElement doBlock, CancellationToken ct)
    {
        var max = ReadMaxIterations(body);
        using var shadow = indexName is null
            ? _scope.Shadow((asName, null))
            : _scope.Shadow((asName, null), (indexName, null));

        var iterations = 0L;
        for (var i = 0; i < count; i++)
        {
            if (++iterations > max && StopAtCap(body))
            {
                break;
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

    // Handles a loop hitting its maxIterations cap: onMaxIterations "warn" logs and exits the loop normally (returns
    // true to break); anything else (the "fail" default) is terminal max_iterations_exceeded.
    private bool StopAtCap(JsonElement loopBody)
    {
        if (string.Equals(OptString(loopBody, "onMaxIterations"), "warn", StringComparison.Ordinal))
        {
            _events.Add(new LogEmitted("warning", $"loop stopped at its maxIterations cap ({ReadMaxIterations(loopBody)})", _clock.GetUtcNow()));
            return true;
        }

        throw new InterpreterException(InterpreterErrorCodes.MaxIterationsExceeded, "loop exceeded its maxIterations cap");
    }

    private async ValueTask<ILocatorHandle> ResolveSelectorAsync(JsonElement body, CancellationToken ct) =>
        await _scope.Sel.ResolveNodeAsync(body.GetProperty("selector"), FrameArg(body), ct);

    // Resolves a node's `in:` (a frame var name, §5.2) to a bound frame handle, or null when absent (page-rooted).
    private IFrameHandle? FrameArg(JsonElement body) =>
        body.TryGetProperty("in", out var inVar) ? _scope.Sel.RequireFrame(inVar.GetString()!) : null;

    private ValueTask<object?> ExprAsync(JsonElement expr, CancellationToken ct) =>
        CrawldadExpression.Parse(expr.GetString()!).EvaluateAsync(_scope, ct);

    private static long ReadMaxIterations(JsonElement body) => body.GetProperty("maxIterations").GetInt64();

    private static string? OptString(JsonElement body, string field) =>
        body.TryGetProperty(field, out var value) ? value.GetString() : null;

    private int Timeout(JsonElement body) =>
        body.TryGetProperty("timeoutMs", out var t) ? t.GetInt32() : _defaultTimeoutMs;

    private RunStats Stats(DateTimeOffset startedAt) =>
        new((long)(_clock.GetUtcNow() - startedAt).TotalMilliseconds, _steps, _requests, 0, _downloads);

    private RunOutcome Failed(string failureClass, string code, string message, DateTimeOffset startedAt) =>
        new(RunStatus.Failed, null, new RunFailureDetail(failureClass, code, message, new RunStepRef(_currentStepIndex, _currentKind)), Stats(startedAt), _events);
}
