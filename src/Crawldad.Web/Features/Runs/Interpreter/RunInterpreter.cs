using System.Security.Cryptography;
using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Crawldad.Web.Infrastructure.Security;
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
/// hits a terminal failure. Config's §8.1 launch/context/route block is parsed into a <see cref="SessionPolicy"/> and
/// handed to the backend at connect (§9.2); the record/replay fake ignores it, a real adapter applies it.
/// </summary>
internal sealed class RunInterpreter
{
    /// <summary>The run-scope var name the restored checkpoint cursor is bound to on resume, so a <c>checkpoint</c>
    /// node's <c>resume</c> sub-program can re-navigate to it (§11). Shared with the semantic walker's scope rule.</summary>
    public const string CheckpointCursorVar = "checkpoint";

    private static readonly IReadOnlySet<string> _defaultRetryOn =
        new HashSet<string>(StringComparer.Ordinal) { "timeout", "pageCrashed" };

    private readonly JsonElement _payload;
    private readonly IReadOnlyDictionary<string, object?> _input;
    private readonly IReadOnlyDictionary<string, object?> _scopeInput;
    private readonly IReadOnlySet<string> _secretRefNames;
    private readonly IBrowserBackendRegistry _registry;
    private readonly IDownloadSinkRegistry _sinks;
    private readonly ISecretStoreRegistry? _secretStores;
    private readonly IRunSecretScope? _secretScope;
    private readonly TimeProvider _clock;
    private readonly string _tenant;
    private readonly IRunObserver? _observer;
    private readonly ResumeState? _resume;
    private readonly IScreenshotStore? _screenshots;
    private readonly RunLimits _limits;
    private readonly List<object> _events = [];
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<Flow>>> _dispatch;

    private RunScope _scope;
    private IPageHandle _page = null!;
    private IBrowserSession? _session;
    private int _steps;
    private int _requests;
    private int _downloads;
    private long _downloadedBytes;
    private int _eventCount;
    private int _checkpointSeq;
    private bool _resumePending;
    private int _defaultTimeoutMs = 120000;
    private bool _screenshotOnFailure = true;
    private int _currentStepIndex;
    private string _currentKind = "config";

    /// <summary>Creates an interpreter for one run (or one resume).</summary>
    /// <param name="payload">The payload document to execute.</param>
    /// <param name="input">The run's input bindings.</param>
    /// <param name="registry">Resolves the backend adapter named by <c>config.backend</c>.</param>
    /// <param name="sinks">Resolves the download sink named by a <c>download.to</c> target.</param>
    /// <param name="clock">The time seam for stats/trace timestamps.</param>
    /// <param name="tenant">The run's tenant (CD-1): threaded into the download/screenshot sinks so their storage is
    /// partitioned per tenant (the tenant in the key/path structure). The content-addressed refs it produces stay
    /// tenant-independent, so the wire result and trace are byte-identical.</param>
    /// <param name="observer">The durable-execution seam (§11): null on the synchronous path (a <c>checkpoint</c> is then
    /// inert, cancellation is never signalled, and no step-trace events are emitted — behaviour and goldens unchanged); the
    /// executor supplies one for the async saga path.</param>
    /// <param name="resume">The checkpoint to resume from (§11), or null for a fresh run from the top.</param>
    /// <param name="screenshots">The failure-screenshot blob store (§13): the executor supplies one; null on the synchronous
    /// path (no observer ⇒ no screenshot-on-failure). Capture is gated on <c>config.screenshotOnFailure</c> and best-effort.</param>
    /// <param name="limits">The server-side per-run resource caps (CD-3/§12): max steps, total downloaded bytes, event count,
    /// and the per-evaluation expression budget. Null uses <see cref="RunLimits.Default"/> (the interpreter unit harness);
    /// the sync endpoint and the async executor both pass the resolved configured caps.</param>
    /// <param name="secretStores">The CD-6 secret-vault registry a <c>fill.secret</c> resolves its <c>secretRef</c> against at
    /// action time; both real run paths supply it. Null (the unit harness) makes a <c>fill.secret</c> a terminal
    /// <c>secret_store_unavailable</c> — a plain-<c>value</c> fill never touches it.</param>
    /// <param name="secretScope">The per-run secret registry a resolved <c>fill.secret</c> value is registered into for
    /// exact-match scrubbing (§12), exactly as the connecting adapters register their credential. Opened by the run's entry
    /// point; the interpreter only registers into the ambient scope.</param>
    public RunInterpreter(
        JsonElement payload,
        IReadOnlyDictionary<string, object?> input,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        TimeProvider clock,
        string tenant,
        IRunObserver? observer = null,
        ResumeState? resume = null,
        IScreenshotStore? screenshots = null,
        RunLimits? limits = null,
        ISecretStoreRegistry? secretStores = null,
        IRunSecretScope? secretScope = null)
    {
        _payload = payload;
        _input = input;
        _registry = registry;
        _sinks = sinks;
        _secretStores = secretStores;
        _secretScope = secretScope;
        _clock = clock;
        _tenant = tenant;
        _observer = observer;
        _resume = resume;
        _screenshots = screenshots;
        _limits = limits ?? RunLimits.Default;
        _checkpointSeq = resume?.Sequence ?? 0; // keep checkpoint sequence monotonic across a resume

        // CD-6: a secretRef input's value is a reference, consumed only by fill.secret. Keep secretRef inputs OUT of the eval
        // scope so no expression can read even the reference — the secret itself is never placed in any scope at all. The
        // declared secretRef names come from the payload's `inputs`, so this holds for inline runs too (no save-time walk).
        _secretRefNames = SecretRefInputs.Names(payload);
        _scopeInput = ScopeVisibleInput(input, _secretRefNames);

        _scope = new RunScope(_scopeInput, _limits.ExpressionStepBudget); // input-only scope for backend resolution; execution rebuilds it per attempt
        _dispatch = BuildDispatch();
    }

    // The scope-visible run input: the supplied input minus every secretRef (CD-6), so `input` in an expression can never
    // surface a secretRef's value OR reference. Returns the same map when no secretRef was supplied (the common case).
    private static IReadOnlyDictionary<string, object?> ScopeVisibleInput(
        IReadOnlyDictionary<string, object?> input, IReadOnlySet<string> secretRefNames)
    {
        if (secretRefNames.Count == 0 || !secretRefNames.Any(input.ContainsKey))
        {
            return input;
        }

        var visible = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            if (!secretRefNames.Contains(key))
            {
                visible[key] = value;
            }
        }

        return visible;
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
            var retryPolicy = ParseRetryPolicy();
            var sessionPolicy = SessionPolicy.FromConfig(_payload.GetProperty("config")); // §8.1 launch/context/route
            _defaultTimeoutMs = sessionPolicy.DefaultTimeoutMs;
            _screenshotOnFailure = ReadScreenshotOnFailure(_payload.GetProperty("config")); // §13 screenshot-on-failure toggle

            var binding = await ResolveBackendAsync(ct);
            if (!_registry.TryResolve(binding.Adapter, out var backend))
            {
                throw new InterpreterException(InterpreterErrorCodes.UnknownBackendAdapter, $"no backend is registered for adapter '{binding.Adapter}'");
            }

            await using var session = await backend.ConnectAsync(binding, sessionPolicy, ct);
            _session = session; // surfaced for stats (region/cacheHits, §10) and the RunTimeline region (§13)
            _page = await session.NewPageAsync(ct);
            await StepAsync(new RunSessionOpened(session.Region, _clock.GetUtcNow()), ct); // §13: carries region to the timeline

            // Assign (don't return) inside the try so the setup catches below still apply, while the happy path falls
            // through to the return below — keeping the try's fall-through exercised.
            outcome = await ExecuteWithRetryAsync(session, retryPolicy, startedAt, ct);
        }
        catch (InterpreterException ex)
        {
            return await ReportFailedAsync("terminal", ex.Code, ex.Message, startedAt, screenshot: false, ct);
        }
        catch (ExpressionEvaluationException ex)
        {
            return await ReportFailedAsync("terminal", ex.Code, ex.Message, startedAt, screenshot: false, ct);
        }
        catch (ExpressionParseException ex)
        {
            return await ReportFailedAsync("terminal", ex.Code, ex.Message, startedAt, screenshot: false, ct);
        }
        catch (FakeBackendException ex)
        {
            return await ReportFailedAsync("terminal", "backend_unavailable", ex.Message, startedAt, screenshot: false, ct);
        }
        catch (BrowserConnectException ex)
        {
            // A real adapter could not connect (bad/absent credential, connect failure). Terminal, like the fake's
            // setup fault. The message is already secret-free by construction (§12). No page bound ⇒ no screenshot.
            return await ReportFailedAsync("terminal", "backend_unavailable", ex.Message, startedAt, screenshot: false, ct);
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
                _scope = new RunScope(_scopeInput, _limits.ExpressionStepBudget); // FRESH scope per attempt — re-evaluate vars, same session (secretRefs excluded, CD-6)
                _scope.Bind(_page);
                if (_resume is null)
                {
                    await EvaluateVarsAsync(ct);
                    await ExecuteStepsAsync(ct);
                }
                else
                {
                    await ResumeAsync(ct); // §11: restore the snapshot + re-enter at the checkpoint — no refetch of earlier work
                }

                var result = JsonValues.ToJson(await EvaluateResultAsync(ct));
                return new RunOutcome(RunStatus.Succeeded, result, null, null, Stats(startedAt), _events);
            }
            catch (RunCancelledSignal)
            {
                // Cooperative cancel (§11): stop between steps and report a partial. The session tears down cleanly when
                // RunAsync's `await using` disposes it — no orphaned backend session. Never retried.
                return await CancelledAsync(startedAt, ct);
            }
            catch (Exception ex) when (ex is BrowserException or CrawldadFailureException or InterpreterException or ExpressionEvaluationException or ExpressionParseException)
            {
                var (code, isRetryableClass, eligibleForRetry) = Classify(ex, policy);
                if (!eligibleForRetry)
                {
                    // A page is bound here, so capture a failure screenshot (§13) before reporting the terminal/exhausted failure.
                    return await ReportFailedAsync(isRetryableClass ? "retryable-exhausted" : "terminal", code, ex.Message, startedAt, screenshot: true, ct);
                }

                // Retryable and permitted: record the attempt, reopen the page on a crash (§3.6), delay, then let the
                // loop re-run the whole program. The last attempt falls through to the exhaustion return below.
                exhaustedCode = code;
                exhaustedMessage = ex.Message;
                if (attempt < policy.MaxAttempts)
                {
                    await EmitAsync(new RunAttemptFailed(attempt, code, _clock.GetUtcNow()), ct);
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

        return await ReportFailedAsync("retryable-exhausted", exhaustedCode, exhaustedMessage, startedAt, screenshot: true, ct);
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
            await StepAsync(new StepStarted(index, HeadKind(step), _clock.GetUtcNow()), ct); // §13 top-level step marker
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
            ["checkpoint"] = Effect(CheckpointAsync),
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
        // Cooperative cancel (§11): honoured BETWEEN steps (at each node boundary), never mid-Playwright-call, so the
        // session tears down cleanly. Null observer (the synchronous path) never cancels — behaviour unchanged.
        if (_observer is { CancellationRequested: true })
        {
            throw new RunCancelledSignal();
        }

        // Max-steps cap (CD-3/§12): the global runaway guard, checked at the single node-dispatch chokepoint so loop-body
        // nodes count too — many loops each under their own maxIterations still multiply into a runaway this bounds.
        if (++_steps > _limits.MaxSteps)
        {
            throw new InterpreterException(InterpreterErrorCodes.MaxStepsExceeded, $"run exceeded its {_limits.MaxSteps}-step cap");
        }

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
        await StepAsync(new Navigated(url, _clock.GetUtcNow()), ct); // §13 (the URL is scrubbed at the sink)
    }

    private async ValueTask WaitForLoadStateAsync(JsonElement body, CancellationToken ct)
    {
        var state = body.GetProperty("state").GetString()!;
        var start = _clock.GetUtcNow();
        await _scope.PageHandle.WaitForLoadStateAsync(state, Timeout(body), ct);
        await StepAsync(new Waited($"loadState:{state}", ElapsedMs(start), _clock.GetUtcNow()), ct); // §13
    }

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
        var start = _clock.GetUtcNow();
        await _scope.PageHandle.RunAndWaitForRequestAsync(
            () => ExecuteBlockAsync(trigger, ct).AsTask(),
            urlPrefix,
            OptString(body, "method"),
            Timeout(body),
            ct);
        _requests++;
        await StepAsync(new Waited("request", ElapsedMs(start), _clock.GetUtcNow()), ct); // §13
    }

    // `state` defaults to "visible" (Playwright's Locator.WaitForAsync default) when omitted — the reference's
    // attachment page-number wait passes no state (:612-614).
    private async ValueTask WaitForAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        var state = OptString(body, "state") ?? "visible";
        var start = _clock.GetUtcNow();
        await handle.WaitForAsync(state, Timeout(body), ct);
        await StepAsync(new Waited($"selector:{state}", ElapsedMs(start), _clock.GetUtcNow()), ct); // §13
    }

    private async ValueTask ClickAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        await handle.ClickAsync(Timeout(body), ct);
        await StepAsync(new Clicked(SelectorLabel(body), _clock.GetUtcNow()), ct); // §13 (selector text scrubbed at the sink)
    }

    private async ValueTask FillAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);

        // CD-6: a fill.secret types a vault-resolved secret, resolved HERE at action time and never routed through the
        // expression value space (a plain-value fill takes the ordinary Expr path). The secret is registered into the run's
        // secret scope (so every sink scrubs it) and typed straight into the field — it is never bound into any scope var.
        if (body.TryGetProperty("secret", out var secretRef))
        {
            var (refName, secret) = await ResolveFillSecretAsync(secretRef.GetString()!, ct);
            await handle.FillAsync(secret, ct);
            await StepAsync(new Filled($"secret:{refName}", _clock.GetUtcNow()), ct); // §13/CD-6: the ref NAME, never the secret
            return;
        }

        var value = ExpressionValues.ToStringValue(await ExprAsync(body.GetProperty("value"), ct));
        await handle.FillAsync(value, ct);
    }

    // Resolves a fill.secret's `input.<name>` reference to its live secret (CD-6): validate the restricted reference shape,
    // read the (reference-only) secretRef input value, resolve it against the tenant-scoped vault, and register the resolved
    // value into the run's secret scope so every sink redacts it — exactly as a connecting adapter registers its credential.
    // Re-runs each time the fill executes, so a checkpoint-resumed run re-resolves naturally and no secret is ever persisted.
    private async ValueTask<(string RefName, string Secret)> ResolveFillSecretAsync(string reference, CancellationToken ct)
    {
        if (!CrawldadExpression.Parse(reference).TryGetInputMemberReference(out var refName) || !_secretRefNames.Contains(refName))
        {
            throw new InterpreterException(
                InterpreterErrorCodes.FillSecretNotSecretRef, $"fill.secret must reference a declared secretRef input via 'input.<name>' (got '{reference}')");
        }

        if (_input.GetValueOrDefault(refName) is not string vaultReference || vaultReference.Length == 0)
        {
            throw new InterpreterException(
                InterpreterErrorCodes.SecretRefMissing, $"secretRef input '{refName}' was not supplied (no reference to resolve)");
        }

        if (_secretStores is null || _secretScope is null)
        {
            // Defensive: both real run paths wire these; the unit harness that omits them never runs a fill.secret payload.
            throw new InterpreterException(InterpreterErrorCodes.UnknownSecretVault, "no secret vault is configured for fill.secret resolution");
        }

        if (!_secretStores.TryResolve(SecretVaults.Config, out var vault))
        {
            throw new InterpreterException(InterpreterErrorCodes.UnknownSecretVault, $"no secret vault is registered for kind '{SecretVaults.Config}'");
        }

        string secret;
        try
        {
            secret = await vault.ResolveForTenantAsync(vaultReference, _tenant, ct);
        }
        catch (SecretNotFoundException ex)
        {
            // Fail-fast at the fill (§8.3 terminal): name only the safe reference, never the secret or the tenant-qualified key.
            throw new InterpreterException(InterpreterErrorCodes.SecretUnresolved, $"secretRef input '{refName}' could not be resolved: {ex.Message}");
        }

        _secretScope.Register(secret); // exact-match scrub for the run's lifetime, including this fill's own trace event
        return (refName, secret);
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
            data = await DrainWithinByteCapAsync(content, ct);
        }

        var hash = SHA256.HashData(data);
        var contentId = AttachmentContentId.FromHash(hash);
        var sha256 = Convert.ToHexStringLower(hash);
        var storedAs = AttachmentContentId.BuildStoredName(contentId, download.SuggestedFilename);
        long sizeBytes = data.Length;

        // exists ⇒ stored:true, no re-upload; else the sink's own success (false ⇒ the reference's handleDownload reject).
        // Both calls carry the run's tenant so the sink partitions storage and probes existence per tenant (CD-1).
        var stored = await sink.ExistsAsync(_tenant, contentId, ct)
            || await sink.StoreAsync(_tenant, new StoredDownload(contentId, storedAs, sizeBytes, sha256), new MemoryStream(data, writable: false), ct);

        _downloads++;
        _scope.Set(body.GetProperty("var").GetString()!, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contentId"] = contentId.ToString(),
            ["sha256"] = sha256,
            ["sizeBytes"] = sizeBytes,
            ["storedAs"] = storedAs,
            ["stored"] = stored,
        });

        // §13 Downloaded: metadata only (blob ref + guessed content type + size + hash) — the bytes streamed to the sink.
        await StepAsync(new Downloaded(storedAs, ContentTypes.ForFile(storedAs), sizeBytes, sha256, _clock.GetUtcNow()), ct);
    }

    // Drains the download to bytes while enforcing the run-wide downloaded-bytes cap AS THE BYTES FLOW (CD-3/§9.3/§12): each
    // chunk advances the cumulative total (across every download in the run) and an over-cap total aborts mid-stream, so a
    // run that would blow the cap fails at the first chunk that crosses it, never after buffering a huge body. The bytes are
    // still materialised for the whole-payload SHA-256 content identity (§9.3) — the guard is on how many may flow, not on
    // buffering the ones that stay under it.
    private async ValueTask<byte[]> DrainWithinByteCapAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            _downloadedBytes += read;
            if (_downloadedBytes > _limits.MaxDownloadedBytes)
            {
                throw new InterpreterException(
                    InterpreterErrorCodes.MaxDownloadBytesExceeded, $"run exceeded its {_limits.MaxDownloadedBytes}-byte download cap");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
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
        var varName = body.GetProperty("var").GetString()!;
        var value = await ExprAsync(body.GetProperty("value"), ct);
        if (body.TryGetProperty("path", out var path))
        {
            await SetPathAsync(varName, path.GetString()!, value, ct);
        }
        else
        {
            _scope.Set(varName, value);
        }

        await StepAsync(new Extracted(varName, ValueRef(value), _clock.GetUtcNow()), ct); // §13 (key + shape ref only, never the value)
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

    private async ValueTask PushAsync(JsonElement body, CancellationToken ct)
    {
        var into = body.GetProperty("into").GetString()!;
        var value = await ExprAsync(body.GetProperty("value"), ct);
        _scope.Push(into, value);
        await StepAsync(new Extracted(into, ValueRef(value), _clock.GetUtcNow()), ct); // §13 (list name + pushed-item shape ref)
    }

    private async ValueTask LogAsync(JsonElement body, CancellationToken ct)
    {
        var level = body.GetProperty("level").GetString()!;
        var message = await CrawldadTemplate.Parse(body.GetProperty("message").GetString()!).RenderAsync(_scope, ct);
        await EmitAsync(new LogEmitted(level, message, _clock.GetUtcNow()), ct);
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

            if (++iterations > max && await StopAtCapAsync(body, ct))
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
            if (++iterations > max && await StopAtCapAsync(body, ct))
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
            if (++iterations > max && await StopAtCapAsync(body, ct))
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
    private async ValueTask<bool> StopAtCapAsync(JsonElement loopBody, CancellationToken ct)
    {
        if (string.Equals(OptString(loopBody, "onMaxIterations"), "warn", StringComparison.Ordinal))
        {
            await EmitAsync(new LogEmitted("warning", $"loop stopped at its maxIterations cap ({ReadMaxIterations(loopBody)})", _clock.GetUtcNow()), ct);
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
        new((long)(_clock.GetUtcNow() - startedAt).TotalMilliseconds, _steps, _requests, _session?.CacheHits ?? 0, _downloads);

    private RunOutcome Failed(string failureClass, string code, string message, DateTimeOffset startedAt) =>
        new(RunStatus.Failed, null, new RunFailureDetail(failureClass, code, message, new RunStepRef(_currentStepIndex, _currentKind)), null, Stats(startedAt), _events);

    // ----- trace emission + screenshot-on-failure (§13) ----------------------

    // Emits a coarse trace event (LogEmitted/RunAttemptFailed): live through the observer on the durable path (in occurrence
    // order), else accumulated for the synchronous endpoint to append at the end (P1 behaviour + goldens unchanged).
    private ValueTask EmitAsync(object traceEvent, CancellationToken ct)
    {
        EnforceEventBudget();
        if (_observer is not null)
        {
            return _observer.EmitAsync(traceEvent, ct);
        }

        _events.Add(traceEvent);
        return ValueTask.CompletedTask;
    }

    // Emits a semantic step-trace event (StepStarted/Navigated/…): ONLY on the durable path (an observer is present). The
    // synchronous path no-ops, so its stream — and every §10 golden — is byte-identical to before (and counts no event).
    private ValueTask StepAsync(object traceEvent, CancellationToken ct)
    {
        if (_observer is null)
        {
            return ValueTask.CompletedTask;
        }

        EnforceEventBudget();
        return _observer.EmitAsync(traceEvent, ct);
    }

    // Max-events cap (CD-3/§12): counts every trace event the interpreter appends to the run stream (step + coarse +
    // checkpoint markers) and trips terminally past the cap. The terminal RunFailed/RunSucceeded is appended by the
    // executor/endpoint after the interpreter returns, so it always lands — the cap bounds the run's own emitted volume.
    private void EnforceEventBudget()
    {
        if (++_eventCount > _limits.MaxEvents)
        {
            throw new InterpreterException(InterpreterErrorCodes.MaxEventsExceeded, $"run exceeded its {_limits.MaxEvents}-event cap");
        }
    }

    // Reports a failure (§13): captures a screenshot when asked (a page is bound), emits StepFailed with its ref, then
    // builds the RunOutcome. On the synchronous path StepAsync no-ops and the screenshot is skipped — Failed() as before.
    private async Task<RunOutcome> ReportFailedAsync(string failureClass, string code, string message, DateTimeOffset startedAt, bool screenshot, CancellationToken ct)
    {
        var screenshotRef = screenshot ? await CaptureFailureScreenshotAsync(ct) : null;
        // The terminal StepFailed marker bypasses the event budget (like the RunFailed the executor appends after): a
        // max_events_exceeded failure must still be able to report itself, not re-trip the cap while emitting its own marker.
        await EmitTerminalStepAsync(new StepFailed(_currentStepIndex, code, screenshotRef, _clock.GetUtcNow()), ct);
        return Failed(failureClass, code, message, startedAt);
    }

    // Emits the terminal StepFailed marker straight to the observer (durable path only), never counting it against the
    // event budget — the failure's own marker always lands, exactly as the terminal RunFailed does.
    private ValueTask EmitTerminalStepAsync(object traceEvent, CancellationToken ct) =>
        _observer is null ? ValueTask.CompletedTask : _observer.EmitAsync(traceEvent, ct);

    // Captures the failing page to blob storage and returns the ref (§13), or null when unavailable: no observer (sync
    // path), no store, disabled via config, or no session/page bound. Best-effort — a crashed page's capture failure is
    // tolerated so a screenshot never masks the run's own failure (§13).
    private async ValueTask<string?> CaptureFailureScreenshotAsync(CancellationToken ct)
    {
        if (_observer is null || _screenshots is null || !_screenshotOnFailure || _session is null)
        {
            return null;
        }

        try
        {
            return await _screenshots.SaveAsync(_tenant, await _scope.PageHandle.ScreenshotAsync(ct), ct);
        }
        catch (BrowserException)
        {
            return null; // a crashed/torn-down page can fail to screenshot — tolerate it
        }
    }

    private static string HeadKind(JsonElement node) => node.EnumerateObject().First().Name;

    private long ElapsedMs(DateTimeOffset start) => (long)(_clock.GetUtcNow() - start).TotalMilliseconds;

    // The click node's declared selector text for the Clicked event (§13): a string selector's raw text, or a structured
    // Sel's raw JSON. The un-rendered form (never the matched element), scrubbed defensively at the sink.
    private static string SelectorLabel(JsonElement body)
    {
        var selector = body.GetProperty("selector");
        return selector.ValueKind == JsonValueKind.String ? selector.GetString()! : selector.GetRawText();
    }

    // A PII-safe shape descriptor of a bound value for the Extracted event (§12/§13): kind + size, NEVER the value.
    private static string ValueRef(object? value) => value switch
    {
        null => "null",
        string s => $"string({s.Length})",
        List<object?> list => $"list({list.Count})",
        IReadOnlyDictionary<string, object?> map => $"map({map.Count})",
        _ => "scalar", // bool / number
    };

    // §13 screenshot-on-failure toggle: captured by default, suppressed by config.screenshotOnFailure:false.
    private static bool ReadScreenshotOnFailure(JsonElement config) =>
        !config.TryGetProperty("screenshotOnFailure", out var flag) || flag.GetBoolean();

    // A cooperative cancel stopped the run between steps (§11). Salvage a partial result (the payload's `result` over the
    // accumulated vars) best-effort — a result expression that faults on the partial state yields no partial, not a crash.
    private async Task<RunOutcome> CancelledAsync(DateTimeOffset startedAt, CancellationToken ct)
    {
        JsonElement? partial;
        try
        {
            partial = JsonValues.ToJson(await EvaluateResultAsync(ct));
        }
        catch (Exception ex) when (ex is BrowserException or CrawldadFailureException or InterpreterException or ExpressionEvaluationException or ExpressionParseException)
        {
            partial = null;
        }

        return new RunOutcome(RunStatus.Cancelled, null, null, partial, Stats(startedAt), _events);
    }

    // ----- checkpoint + resume (§11) -----------------------------------------

    // checkpoint records the resumable position durably through the observer. On the synchronous path (no observer) it is
    // inert, so the acceptance goldens are unchanged. On the FIRST checkpoint of a resumed run it also runs the payload's
    // `resume` sub-program to re-establish the fresh browser session at the restored cursor before continuing.
    private async ValueTask CheckpointAsync(JsonElement body, CancellationToken ct)
    {
        if (_observer is null)
        {
            return;
        }

        if (_resumePending)
        {
            _resumePending = false;
            await ExecuteBlockAsync(body.GetProperty("resume"), ct); // re-navigate to the restored cursor (bound to `checkpoint`)
        }

        var name = body.GetProperty("name").GetString()!;
        var cursor = JsonValues.ToJson(await ExprAsync(body.GetProperty("cursor"), ct));
        EnforceEventBudget(); // the checkpoint marker is a stream event too (CD-3/§12)
        await _observer.CheckpointReachedAsync(new CheckpointSnapshot(name, ++_checkpointSeq, _currentStepIndex, cursor, SnapshotVarsJson()), ct);
    }

    // The accumulated declared-var state to persist at a checkpoint: everything except `input` (re-supplied on resume) and
    // opaque locator/frame handles (transient — re-derived by the resumed loop body against the fresh session).
    private JsonElement SnapshotVarsJson()
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in _scope.Vars)
        {
            if (!string.Equals(key, "input", StringComparison.Ordinal) && value is not (ILocatorHandle or IFrameHandle))
            {
                snapshot[key] = value;
            }
        }

        return JsonValues.ToJson(snapshot);
    }

    // Resumes execution (§11) from the checkpoint the executor restored: re-seed the accumulated vars, bind the cursor to
    // the `checkpoint` var for the resume sub-program, then re-enter the program at the checkpoint's enclosing top-level
    // step and run to the end. The first checkpoint node re-navigates (CheckpointAsync's _resumePending path); earlier
    // top-level steps (the initial navigation/search) are skipped, so completed work is never refetched.
    private async ValueTask ResumeAsync(CancellationToken ct)
    {
        foreach (var declared in _resume!.Vars.EnumerateObject())
        {
            _scope.Set(declared.Name, JsonValues.FromJson(declared.Value));
        }

        _scope.Set(CheckpointCursorVar, JsonValues.FromJson(_resume.Cursor));
        _resumePending = true;

        var index = 0;
        foreach (var step in _payload.GetProperty("steps").EnumerateArray())
        {
            if (index >= _resume.StepIndex)
            {
                _currentStepIndex = index;
                await StepAsync(new StepStarted(index, HeadKind(step), _clock.GetUtcNow()), ct); // §13 marker for each resumed step
                await ExecuteNodeAsync(step, ct);
            }

            index++;
        }
    }
}
