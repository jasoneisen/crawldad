using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>Control-flow signal bubbling out of a block (<c>break</c>/<c>continue</c>), consumed by the nearest loop.</summary>
internal enum Flow
{
    /// <summary>Fell through normally.</summary>
    Normal,

    /// <summary>A <c>break</c> fired — the enclosing loop stops.</summary>
    Break,

    /// <summary>A <c>continue</c> fired — the enclosing loop advances to the next iteration.</summary>
    Continue,
}

/// <summary>The interpreter: executes one payload against a backend and shapes its <c>result</c>. A parse pre-pass
/// rejects unknown head keys and missing <c>maxIterations</c> before any side effect; dispatch is by a shared table so
/// validation and execution agree by construction. A retryable failure re-runs with a fresh scope on the same session.</summary>
internal sealed class RunInterpreter
{
    /// <summary>The run-scope var name the restored checkpoint cursor is bound to on resume, so a <c>checkpoint</c>
    /// node's <c>resume</c> sub-program can re-navigate to it. Shared with the semantic walker's scope rule.</summary>
    public const string CheckpointCursorVar = "checkpoint";

    /// <summary>The terminal failure code for a backend that could not be connected/set up — the connect boundary's
    /// single code, whether an auth-shaped fault failed fast or a transient one exhausted <c>config.connectRetry</c>.</summary>
    private const string _backendUnavailableCode = "backend_unavailable";

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
    private readonly IFixtureRecorder? _recorder; // record mode: banks page states + transitions into a fixture set; null on every ordinary run
    private readonly RunLimits _limits;
    private readonly List<object> _events = [];
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<Flow>>> _dispatch;

    private readonly ISelectorMissSink _missSink;
    private readonly HashSet<string> _seenMissSelectors = new(StringComparer.Ordinal); // dedupe: one SelectorMiss event per distinct selector per run

    private RunScope _scope;
    private IPageHandle _page = null!;
    private IBrowserSession? _session;
    private int _steps;
    private int _requests;
    private int _downloads;
    private int _selectorMisses;
    private long _downloadedBytes;
    private long _capturedBytes;
    private int _eventCount;
    private int _checkpointSeq;
    private bool _resumePending;
    private int _defaultTimeoutMs = 120000;
    private bool _screenshotOnFailure = true;
    private bool _strictExtraction; // config.strictExtraction: when true, ANY selector miss is terminal (default false ⇒ soft)
    private IDownloadSink? _captureOnFailureSink; // config.captureOnFailure's resolved BYO sink, or null when disabled
    private int _currentStepIndex;
    private string _currentKind = "config";

    /// <summary>Creates an interpreter for one run (or one resume). <paramref name="tenant"/> partitions download/
    /// screenshot storage; <paramref name="observer"/>/<paramref name="resume"/>/<paramref name="screenshots"/> are null
    /// on the synchronous path; <paramref name="limits"/> defaults to <see cref="RunLimits.Default"/> when null;
    /// <paramref name="recorder"/> is non-null only on the record-mode path and banks page states/transitions as the run executes.</summary>
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
        IRunSecretScope? secretScope = null,
        IFixtureRecorder? recorder = null)
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
        _recorder = recorder;
        _limits = limits ?? RunLimits.Default;
        _checkpointSeq = resume?.Sequence ?? 0; // keep checkpoint sequence monotonic across a resume

        // A secretRef input's value is a reference, consumed only by fill.secret. Keep secretRef inputs OUT of the eval
        // scope so no expression can read even the reference — the secret itself is never placed in any scope at all. The
        // declared secretRef names come from the payload's `inputs`, so this holds for inline runs too (no save-time walk).
        _secretRefNames = SecretRefInputs.Names(payload);
        _scopeInput = ScopeVisibleInput(input, _secretRefNames);

        _missSink = new SelectorMissReporter(this); // one sink for the whole run; every RunScope (per attempt) reports through it
        _scope = new RunScope(_scopeInput, _limits.ExpressionStepBudget, _missSink); // input-only scope for backend resolution; execution rebuilds it per attempt
        _dispatch = BuildDispatch();
    }

    // The scope-visible run input: the supplied input minus every secretRef, so `input` in an expression can never
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
    public async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();
        RunOutcome outcome;
        try
        {
            ValidateProgram(); // reject unknown head keys / missing maxIterations before any side effect
            var retryPolicy = ParseRetryPolicy();
            var connectRetryPolicy = ParseConnectRetryPolicy(); // bounded retry for the connect boundary (separate from the program retry)
            var sessionPolicy = SessionPolicy.FromConfig(_payload.GetProperty("config")); // launch/context/route
            _defaultTimeoutMs = sessionPolicy.DefaultTimeoutMs;
            _screenshotOnFailure = ReadScreenshotOnFailure(_payload.GetProperty("config")); // screenshot-on-failure toggle
            _strictExtraction = ReadStrictExtraction(_payload.GetProperty("config")); // strict-extraction toggle: every selector miss terminal
            await ResolveCaptureOnFailureAsync(ct); // resolve the capture-on-failure BYO sink up front (a bad target fails at setup, not silently at failure time)

            var binding = await ResolveBackendAsync(ct);
            if (!_registry.TryResolve(binding.Adapter, out var backend))
            {
                throw new InterpreterException(InterpreterErrorCodes.UnknownBackendAdapter, $"no backend is registered for adapter '{binding.Adapter}'");
            }

            await using var session = await ConnectWithRetryAsync(backend, binding, sessionPolicy, connectRetryPolicy, ct);
            _session = session; // surfaced for stats (region/cacheHits) and the RunTimeline region
            _page = await session.NewPageAsync(ct);
            await StepAsync(new RunSessionOpened(session.Region, _clock.GetUtcNow()), ct); // carries region to the timeline

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
            return await ReportFailedAsync("terminal", _backendUnavailableCode, ex.Message, startedAt, screenshot: false, ct);
        }
        catch (FixtureDivergenceException ex)
        {
            // A strict tenant fixture replay diverged (unrecorded goto/click): terminal, classified by the exception's own code, naming the miss; no page screenshot.
            return await ReportFailedAsync("terminal", ex.Code, ex.Message, startedAt, screenshot: false, ct);
        }
        catch (BrowserConnectException ex)
        {
            // A real adapter could not connect (bad/absent credential, or a transient fault that outlived connectRetry).
            // Terminal, like the fake's setup fault — an auth-shaped fault fails fast, a transient one only after the
            // bounded attempts are spent (its message then reflects them). Secret-free by construction; no page ⇒ no screenshot.
            return await ReportFailedAsync("terminal", _backendUnavailableCode, ex.Message, startedAt, screenshot: false, ct);
        }

        return outcome;
    }

    // ----- retry/resilience layer -------------------------------------

    private async Task<RunOutcome> ExecuteWithRetryAsync(IBrowserSession session, RetryPolicy policy, DateTimeOffset startedAt, CancellationToken ct)
    {
        var exhaustedCode = "";
        var exhaustedMessage = "";
        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            try
            {
                _scope = new RunScope(_scopeInput, _limits.ExpressionStepBudget, _missSink); // FRESH scope per attempt (secretRefs excluded)
                _scope.Bind(_page);
                _recorder?.Reset(); // discard any partial recording from a prior (retried) attempt — bank only the successful pass
                if (_resume is null)
                {
                    await EvaluateVarsAsync(ct);
                    await ExecuteStepsAsync(ct);
                }
                else
                {
                    await ResumeAsync(ct); // restore the snapshot + re-enter at the checkpoint — no refetch of earlier work
                }

                var result = JsonValues.ToJson(await EvaluateResultAsync(ct));
                await FinalizeRecordingAsync(ct); // settle the final DOM into the recorded manifest (a no-op on every ordinary run)
                return new RunOutcome(RunStatus.Succeeded, result, null, null, Stats(startedAt), _events);
            }
            catch (RunCancelledSignal)
            {
                // Cooperative cancel: stop between steps and report a partial. The session tears down cleanly when
                // RunAsync's `await using` disposes it — no orphaned backend session. Never retried.
                return await CancelledAsync(startedAt, ct);
            }
            catch (Exception ex) when (ex is BrowserException or CrawldadFailureException or InterpreterException or ExpressionEvaluationException or ExpressionParseException)
            {
                var (code, isRetryableClass, eligibleForRetry) = Classify(ex, policy);
                if (!eligibleForRetry)
                {
                    // A page is bound here, so capture a failure screenshot before reporting the terminal/exhausted failure.
                    return await ReportFailedAsync(isRetryableClass ? "retryable-exhausted" : "terminal", code, ex.Message, startedAt, screenshot: true, ct);
                }

                // Retryable and permitted: record the attempt, reopen the page on a crash (unless onPageCrashed:fail
                // opted out — the retry then re-runs against the page it crashed on), delay, then let the loop re-run the
                // whole program. The last attempt falls through to the exhaustion return below.
                exhaustedCode = code;
                exhaustedMessage = ex.Message;
                if (attempt < policy.MaxAttempts)
                {
                    await EmitAsync(new RunAttemptFailed(attempt, code, _clock.GetUtcNow()), ct);
                    if (ex is BrowserPageCrashedException && policy.OnPageCrashed == PageCrashHandlingStrategy.ReopenPage)
                    {
                        await ReopenPageAsync(session, ct); // reopenPage: close the crashed page, open a fresh one on the SAME context, rebind
                    }

                    await BackoffAsync(policy, attempt, ct); // strategy-scaled backoff on the injected clock (honours the deadline)
                }
            }
        }

        return await ReportFailedAsync("retryable-exhausted", exhaustedCode, exhaustedMessage, startedAt, screenshot: true, ct);
    }

    // Waits the strategy's backoff before the next attempt, on the injected clock: DelayMs scaled by the just-failed
    // attempt number, saturated at maxDelayMs, and (when enabled) spread across [0, delay] with full jitter. A 0 delay
    // (constant delayMs:0, or a jitter draw of 0) skips the wait. The wait honours run cancellation/deadline — a delay the
    // deadline elapses under throws OperationCanceledException here, terminal, exactly like the constant delay it replaced.
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "Retry jitter is a thundering-herd mitigation, not a security primitive — a fast non-cryptographic draw is exactly right.")]
    private async ValueTask BackoffAsync(RetryPolicy policy, int failedAttempt, CancellationToken ct)
    {
        var delayMs = policy.BackoffDelayMs(failedAttempt);
        if (policy.Jitter)
        {
            delayMs = RetryBackoff.FullJitter(delayMs, Random.Shared.NextDouble());
        }

        if (delayMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _clock, ct);
        }
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

    // Settles the final DOM into the recorder on a successful run (closing the last open transition); a no-op on every
    // ordinary run, so behaviour and goldens are unchanged unless record mode is active.
    private ValueTask FinalizeRecordingAsync(CancellationToken ct) =>
        _recorder is null ? ValueTask.CompletedTask : _recorder.FinalizeAsync(_scope.PageHandle, ct);

    private async ValueTask ReopenPageAsync(IBrowserSession session, CancellationToken ct)
    {
        await CloseQuietlyAsync(_page, ct);
        _page = await session.NewPageAsync(ct); // SAME session/context, rebound into the next attempt's scope
    }

    /// <summary>Closes a page best-effort, tolerating a crashed page's close failure. Internal for direct testing.</summary>
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
        if (NodeJson.OptionalObject(_payload.GetProperty("config"), "retry") is not { } retry)
        {
            return new RetryPolicy(1, 0, RetryBackoffStrategy.Constant, null, false, _defaultRetryOn, PageCrashHandlingStrategy.ReopenPage); // absent ⇒ a single attempt
        }

        var maxAttempts = NodeJson.OptionalInt(retry, "maxAttempts", 1);
        var delayMs = NodeJson.OptionalInt(retry, "delayMs", 0);
        var backoff = ParseBackoff(retry);
        var maxDelayMs = retry.TryGetProperty("maxDelayMs", out _) ? NodeJson.OptionalInt(retry, "maxDelayMs", 0) : (int?)null; // absent ⇒ uncapped
        var jitter = NodeJson.OptionalBool(retry, "jitter", false);
        var retryOn = retry.TryGetProperty("retryOn", out _)
            ? new HashSet<string>(NodeJson.OptionalStringArray(retry, "retryOn"), StringComparer.Ordinal) // present (even empty) overrides the default
            : _defaultRetryOn;
        var onPageCrashed = ParseOnPageCrashed(retry);
        return new RetryPolicy(maxAttempts, delayMs, backoff, maxDelayMs, jitter, retryOn, onPageCrashed);
    }

    // The backoff strategy: absent ⇒ constant (the pre-backoff default, so an existing payload is unchanged). The schema
    // rejects an unknown token at save/validate time; an inline run skips the schema, so an unrecognised strategy is
    // classified terminally HERE rather than silently applying a constant delay it never asked for.
    private static RetryBackoffStrategy ParseBackoff(JsonElement retry)
    {
        if (NodeJson.OptionalString(retry, "backoff") is not { } token)
        {
            return RetryBackoffStrategy.Constant;
        }

        return RetryBackoff.TryParse(token, out var strategy)
            ? strategy
            : throw new InterpreterException(InterpreterErrorCodes.InvalidRetryBackoff, $"config.retry.backoff '{token}' is not a known strategy (constant, linear, exponential)");
    }

    // The page-crash handling: absent ⇒ reopenPage (the pre-existing unconditional reopen, so an existing payload is
    // unchanged). The schema rejects an unknown token at save/validate time; an inline run skips the schema, so an
    // unrecognised value is classified terminally HERE rather than silently reopening a page it never asked to reopen.
    // Orthogonal to retryOn: retryOn decides WHETHER a pageCrashed is retried; this decides what happens to the page first.
    private static PageCrashHandlingStrategy ParseOnPageCrashed(JsonElement retry)
    {
        if (NodeJson.OptionalString(retry, "onPageCrashed") is not { } token)
        {
            return PageCrashHandlingStrategy.ReopenPage;
        }

        return PageCrashHandling.TryParse(token, out var strategy)
            ? strategy
            : throw new InterpreterException(InterpreterErrorCodes.InvalidRetryOnPageCrashed, $"config.retry.onPageCrashed '{token}' is not a known option (reopenPage, fail)");
    }

    private sealed record RetryPolicy(int MaxAttempts, int DelayMs, RetryBackoffStrategy Backoff, int? MaxDelayMs, bool Jitter, IReadOnlySet<string> RetryOn, PageCrashHandlingStrategy OnPageCrashed)
    {
        // The pre-jitter backoff before the retry after the (1-based) failed attempt: DelayMs scaled by the strategy, capped at MaxDelayMs.
        public int BackoffDelayMs(int failedAttempt) => RetryBackoff.DelayMs(Backoff, DelayMs, failedAttempt, MaxDelayMs);
    }

    // ----- connect boundary (its own bounded retry) --------------------

    // The connect happens ONCE per run, before the program — and config.retry never reaches it (that policy reuses an
    // already-established session; it never reconnects). config.connectRetry is the separate knob for the connect
    // boundary: a bounded, off-by-default retry so a run submitted during ordinary tunnel churn (a cloudflared edge
    // reconnect, the connector's own re-register cycle) survives instead of failing outright on a transient blip.
    private async Task<IBrowserSession> ConnectWithRetryAsync(
        IBrowserBackend backend, BackendBinding binding, SessionPolicy sessionPolicy, ConnectRetryPolicy policy, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                // A fresh ConnectAsync each attempt re-resolves the credentialRef (the adapter's own resolve step) — so a
                // connector's mid-window re-registration (the tunnel URL rotated under a stable name) is picked up on the
                // next try, and the newly-resolved secret is re-registered into the run's scrub scope. The raw secret is
                // resolved and registered entirely inside the adapter; it never reaches this layer to be logged or emitted.
                return await backend.ConnectAsync(binding, sessionPolicy, ct);
            }
            catch (BrowserConnectException ex)
            {
                // Fail fast on an auth-shaped/permanent fault, or once the bounded attempts are spent. The terminal
                // classification stays backend_unavailable either way (RunAsync's catch); after ≥1 retry the message
                // reflects the attempts made, while a single-shot failure surfaces the original (secret-free) message verbatim.
                if (!ex.Retryable || attempt >= policy.MaxAttempts)
                {
                    throw attempt > 1
                        ? new BrowserConnectException($"{ex.Message} (after {attempt} connect attempts)")
                        : ex;
                }

                // Transient, attempts remain: record a secret-free attempt marker, back off on the injected clock
                // (honouring run cancellation/deadline — a delay that would outlive the deadline throws here, terminal),
                // then re-enter the loop for a fresh connect.
                await EmitAsync(new RunConnectAttemptFailed(attempt, _backendUnavailableCode, _clock.GetUtcNow()), ct);
                if (policy.DelayMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(policy.DelayMs), _clock, ct);
                }
            }
        }
    }

    // config.connectRetry drives the loop above. Absent ⇒ ConnectRetryPolicy.None (maxAttempts 1) — the pre-existing
    // single-shot connect, so behaviour is unchanged unless a run opts in. Bounds are clamped here (a saved payload is
    // additionally rejected out-of-range by the schema; an inline run skips the schema, so the clamp is its floor/cap):
    // the connect holds a backend admission slot across REAL network waits, so — unlike config.retry, which reuses an
    // in-hand session — an unbounded maxAttempts/delayMs could pin a slot far past reason, hence a deliberate cap.
    private ConnectRetryPolicy ParseConnectRetryPolicy()
    {
        if (NodeJson.OptionalObject(_payload.GetProperty("config"), "connectRetry") is not { } connectRetry)
        {
            return ConnectRetryPolicy.None;
        }

        var maxAttempts = Math.Clamp(NodeJson.OptionalInt(connectRetry, "maxAttempts", 1), 1, ConnectRetryPolicy.MaxAttemptsCap);
        var delayMs = Math.Clamp(NodeJson.OptionalInt(connectRetry, "delayMs", 0), 0, ConnectRetryPolicy.MaxDelayMsCap);
        return new ConnectRetryPolicy(maxAttempts, delayMs);
    }

    private sealed record ConnectRetryPolicy(int MaxAttempts, int DelayMs)
    {
        /// <summary>The cap on attempts (a handful suffices — tunnel churn clears in seconds; more just burns the deadline).</summary>
        public const int MaxAttemptsCap = 10;

        /// <summary>The cap on the per-attempt backoff (60 s — a longer single connect wait is unreasonable; the run deadline still governs the total).</summary>
        public const int MaxDelayMsCap = 60000;

        /// <summary>Absent config.connectRetry ⇒ a single connect attempt, no backoff (the pre-existing single-shot behaviour).</summary>
        public static ConnectRetryPolicy None { get; } = new(1, 0);
    }

    // ----- parse-time validation ----------------------------

    // The run-time structural pre-pass: the SAME PayloadValidator save-time uses (one implementation, no divergence).
    // It rejects unknown head keys and missing maxIterations before any side effect. The run path deliberately skips
    // the full save-time semantic pass, so defined-before-use/expression-parse faults surface at evaluation instead.
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
        var expr = NodeJson.RequireString(_payload.GetProperty("config"), "backend");
        if (await CrawldadExpression.Parse(expr).EvaluateAsync(_scope, ct) is not Dictionary<string, object?> map)
        {
            throw new InterpreterException(InterpreterErrorCodes.InvalidBackendBinding, "config.backend must resolve to a { adapter, options } object");
        }

        var adapter = map.GetValueOrDefault("adapter") as string
            ?? throw new InterpreterException(InterpreterErrorCodes.InvalidBackendBinding, "config.backend.adapter is required");
        var options = map.GetValueOrDefault("options") as IReadOnlyDictionary<string, object?>;
        var credentialRef = map.GetValueOrDefault("credentialRef") as string;
        return new BackendBinding(adapter, credentialRef, options, _tenant); // the run's tenant scopes credential resolution
    }

    // Resolves config.captureOnFailure's BYO sink ONCE at setup, against the input-only scope (like config.backend), so a
    // misconfigured target fails the run loudly at setup rather than silently at failure time. Absent ⇒ disabled: there is
    // no default target, so — unlike screenshotOnFailure (default on, streaming to the operator's own screenshot store) —
    // capture-on-failure is opt-in by supplying a tenant storageTarget for the failing page's HTML to land in.
    private async ValueTask ResolveCaptureOnFailureAsync(CancellationToken ct)
    {
        if (NodeJson.OptionalObject(_payload.GetProperty("config"), "captureOnFailure") is not { } captureOnFailure)
        {
            return;
        }

        _captureOnFailureSink = ResolveSink(
            await ExprAsync(captureOnFailure, "to", ct), InterpreterErrorCodes.InvalidCaptureTarget, InterpreterErrorCodes.UnknownCaptureSink, "captureOnFailure");
    }

    private async ValueTask EvaluateVarsAsync(CancellationToken ct)
    {
        if (!_payload.TryGetProperty("vars", out var vars))
        {
            return;
        }

        foreach (var declared in NodeJson.RequireObjectValue(vars, "vars").EnumerateObject())
        {
            _scope.Set(declared.Name, await EvaluateVarValueAsync(declared.Value, ct));
        }
    }

    // A string var value is an expression; a non-string is a JSON literal (the fragment's `pageResults: []`).
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
            await StepAsync(new StepStarted(index, HeadKind(step), _clock.GetUtcNow()), ct); // top-level step marker
            await ExecuteNodeAsync(step, ct); // top-level break/continue (outside any loop) is a no-op
            index++;
        }
    }

    private ValueTask<object?> EvaluateResultAsync(CancellationToken ct) =>
        CrawldadExpression.Parse(NodeJson.RequireString(_payload, "result")).EvaluateAsync(_scope, ct);

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
            ["screenshot"] = Effect(ScreenshotAsync),
            ["locate"] = Effect(LocateAsync),
            ["download"] = Effect(DownloadAsync),
            ["capture"] = Effect(CaptureAsync),
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
        // Cooperative cancel: honoured BETWEEN steps (at each node boundary), never mid-Playwright-call, so the
        // session tears down cleanly. Null observer (the synchronous path) never cancels — behaviour unchanged.
        if (_observer is { CancellationRequested: true })
        {
            throw new RunCancelledSignal();
        }

        // Max-steps cap: the global runaway guard, checked at the single node-dispatch chokepoint so loop-body
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
        var url = await CrawldadTemplate.Parse(NodeJson.RequireString(body, "url")).RenderAsync(_scope, ct);
        await _scope.PageHandle.GotoAsync(url, OptString(body, "waitUntil"), Timeout(body), ct);
        _requests++;
        if (_recorder is not null)
        {
            await _recorder.OnNavigatedAsync(url, _scope.PageHandle, ct); // bank the landing DOM as a gotoUrl state
        }

        await StepAsync(new Navigated(url, _clock.GetUtcNow()), ct); // the URL is scrubbed at the sink
    }

    private async ValueTask WaitForLoadStateAsync(JsonElement body, CancellationToken ct)
    {
        var state = NodeJson.RequireString(body, "state");
        var start = _clock.GetUtcNow();
        await _scope.PageHandle.WaitForLoadStateAsync(state, Timeout(body), ct);
        await StepAsync(new Waited($"loadState:{state}", ElapsedMs(start), _clock.GetUtcNow()), ct);
    }

    // frame binds a FrameLocator handle to a var; `in:` on later nodes/Sels roots resolution inside it. The
    // selector is the iframe element's CSS Tmpl (Playwright FrameLocator takes a string, so a Sel here is its string form).
    private async ValueTask FrameAsync(JsonElement body, CancellationToken ct)
    {
        var selector = await CrawldadTemplate.Parse(NodeJson.RequireString(body, "selector")).RenderAsync(_scope, ct);
        _scope.Set(NodeJson.RequireString(body, "var"), _scope.PageHandle.FrameLocator(selector));
    }

    // addStyleTag injects CSS (data, not code); the reference forces record tabs visible.
    private async ValueTask AddStyleTagAsync(JsonElement body, CancellationToken ct)
    {
        var content = await CrawldadTemplate.Parse(NodeJson.RequireString(body, "content")).RenderAsync(_scope, ct);
        await _scope.PageHandle.AddStyleTagAsync(content, ct);
    }

    private async ValueTask WaitForRequestAsync(JsonElement body, CancellationToken ct)
    {
        var urlPrefix = await CrawldadTemplate.Parse(NodeJson.RequireString(body, "urlPrefix")).RenderAsync(_scope, ct);
        var trigger = NodeJson.RequireElement(body, "trigger");
        var start = _clock.GetUtcNow();

        // Arm the emit the trigger's click will record (so a strict replay's postback wait matches), disarming once the
        // wait completes. Null recorder ⇒ both are no-ops and the wait is byte-identical to an ordinary run.
        _recorder?.SetPendingEmit(urlPrefix, OptString(body, "method"));
        await _scope.PageHandle.RunAndWaitForRequestAsync(
            () => ExecuteBlockAsync(trigger, ct).AsTask(),
            urlPrefix,
            OptString(body, "method"),
            Timeout(body),
            ct);
        _recorder?.ClearPendingEmit();
        _requests++;
        await StepAsync(new Waited("request", ElapsedMs(start), _clock.GetUtcNow()), ct);
    }

    // `state` defaults to "visible" (Playwright's Locator.WaitForAsync default) when omitted — the reference's
    // attachment page-number wait passes no state.
    private async ValueTask WaitForAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        var state = OptString(body, "state") ?? "visible";
        var start = _clock.GetUtcNow();
        await handle.WaitForAsync(state, Timeout(body), ct);
        await StepAsync(new Waited($"selector:{state}", ElapsedMs(start), _clock.GetUtcNow()), ct);
    }

    private async ValueTask ClickAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        if (_recorder is not null)
        {
            // Record the click's from-state and open a transition BEFORE the click fires — a string CSS selector is the
            // recordable form; a structured/non-CSS selector (css null) or an in-frame click is rejected by the recorder.
            var selector = body.GetProperty("selector");
            var css = selector.ValueKind == JsonValueKind.String ? selector.GetString() : null;
            await _recorder.OnClickAsync(css, body.TryGetProperty("in", out _), _scope.PageHandle, ct);
        }

        await handle.ClickAsync(Timeout(body), ct);
        await StepAsync(new Clicked(SelectorLabel(body), _clock.GetUtcNow()), ct); // selector text scrubbed at the sink
    }

    private async ValueTask FillAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);

        // A fill.secret types a vault-resolved secret, resolved HERE at action time and never routed through the
        // expression value space (a plain-value fill takes the ordinary Expr path). The secret is registered into the run's
        // secret scope (so every sink scrubs it) and typed straight into the field — it is never bound into any scope var.
        if (body.TryGetProperty("secret", out var secretRef))
        {
            var (refName, secret) = await ResolveFillSecretAsync(NodeJson.RequireStringValue(secretRef, "fill.secret"), ct);
            await handle.FillAsync(secret, ct);
            await StepAsync(new Filled($"secret:{refName}", _clock.GetUtcNow()), ct); // the ref NAME, never the secret
            return;
        }

        var value = ExpressionValues.ToStringValue(await ExprAsync(body, "value", ct));
        await handle.FillAsync(value, ct);
    }

    // Resolves a fill.secret's `input.<name>` reference to its live secret: validate the restricted reference shape,
    // resolve it against the tenant-scoped vault, and register the value into the run's secret scope so every sink
    // redacts it — exactly as a connecting adapter registers its credential. Re-runs each fill; no secret is ever persisted.
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
            // Fail-fast at the fill (terminal): name only the safe reference, never the secret or the tenant-qualified key.
            throw new InterpreterException(InterpreterErrorCodes.SecretUnresolved, $"secretRef input '{refName}' could not be resolved: {ex.Message}");
        }

        _secretScope.RegisterFormSecret(secret); // exact-match scrub (lower form floor) for the run's lifetime, including this fill's own trace event
        return (refName, secret);
    }

    private async ValueTask ClearAsync(JsonElement body, CancellationToken ct)
    {
        var handle = await ResolveSelectorAsync(body, ct);
        await handle.ClearAsync(ct);
    }

    // An explicit screenshot node: the author-authored analogue of screenshot-on-failure, reusing the same
    // IScreenshotStore seam (inert on the synchronous path, like every step-trace event there). Its PNG bytes are exempt
    // from the download byte cap; unlike the best-effort failure capture, a fault here propagates as the run's own failure.
    private async ValueTask ScreenshotAsync(JsonElement body, CancellationToken ct)
    {
        if (_observer is null || _screenshots is null)
        {
            return;
        }

        var nameLabel = NodeJson.OptionalString(body, "name");
        var name = nameLabel is null ? null : await CrawldadTemplate.Parse(nameLabel).RenderAsync(_scope, ct);
        var png = await _scope.PageHandle.ScreenshotAsync(ct);
        var screenshotRef = await _screenshots.SaveAsync(_tenant, png, ct);
        await StepAsync(new Screenshotted(screenshotRef, name, png.Length, _clock.GetUtcNow()), ct); // ref + metadata only, never the bytes
    }

    // ----- locate (both forms) ----------------------------------------------

    private async ValueTask LocateAsync(JsonElement body, CancellationToken ct)
    {
        var handle = body.TryGetProperty("from", out var from)
            ? await LocateFromHandleAsync(body, NodeJson.RequireStringValue(from, "locate 'from'"), ct)
            : await LocateFromSelectorAsync(body, ct);

        _scope.Set(NodeJson.RequireString(body, "var"), handle);
    }

    private async ValueTask<ILocatorHandle> LocateFromHandleAsync(JsonElement body, string fromVar, CancellationToken ct)
    {
        var handle = _scope.Sel.RequireHandle(fromVar);

        if (body.TryGetProperty("filter", out var filter))
        {
            var regex = NodeJson.RequireString(NodeJson.RequireObjectValue(filter, "filter"), "hasTextRegex");
            handle = handle.Filter(await CrawldadTemplate.Parse(regex).RenderAsync(_scope, ct));
        }

        if (body.TryGetProperty("nth", out var nth))
        {
            // Classify a non-integral/non-numeric/out-of-range nth (terminal type_error / index_out_of_range),
            // never the raw (int)(long) unbox that escaped the retry layer as a 500 (or silently truncated a > int value).
            handle = handle.Nth(ExpressionValues.RequireNthIndex(await ExprAsync(nth, ct)));
        }

        if (NodeJson.OptionalBool(body, "first", false))
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
            var css = await CrawldadTemplate.Parse(NodeJson.RequireString(body, "selector")).RenderAsync(_scope, ct);
            return _scope.Sel.RequireHandle(NodeJson.RequireStringValue(baseVar, "base")).Locator(css);
        }

        return await _scope.Sel.ResolveNodeAsync(NodeJson.RequireElement(body, "selector"), FrameArg(body), ct);
    }

    // ----- download + capture (the BYO-storage artifact channels) -----

    // Runs the trigger, drains the download to compute the content identity, and streams it to the target sink —
    // idempotently: an already-present blob short-circuits to stored:true WITHOUT re-uploading. Binds
    // dl = { contentId, sha256, sizeBytes, storedAs, stored }; download failure/timeout is retryable.
    private async ValueTask DownloadAsync(JsonElement body, CancellationToken ct)
    {
        _recorder?.RejectUnrecordable("download"); // record mode does not capture downloads in v1 — fail the record run classified
        var sink = ResolveSink(await ExprAsync(body, "to", ct), InterpreterErrorCodes.InvalidDownloadTarget, InterpreterErrorCodes.UnknownDownloadSink, "download");
        var trigger = NodeJson.RequireElement(body, "trigger");
        var download = await _scope.PageHandle.RunAndWaitForDownloadAsync(
            () => ExecuteBlockAsync(trigger, ct).AsTask(), Timeout(body), ct);

        byte[] data;
        await using (var content = await download.OpenReadAsync(ct))
        {
            data = await DrainWithinByteCapAsync(content, ct);
        }

        var artifact = await StoreArtifactAsync(sink, data, download.SuggestedFilename, ct);
        _downloads++;
        _scope.Set(NodeJson.RequireString(body, "var"), artifact.Binding);

        // Downloaded: metadata only (blob ref + guessed content type + size + hash) — the bytes streamed to the sink.
        await StepAsync(new Downloaded(artifact.StoredAs, ContentTypes.ForFile(artifact.StoredAs), artifact.SizeBytes, artifact.Sha256, _clock.GetUtcNow()), ct);
    }

    // The content-addressed stored name a capture takes: capture bytes are always HTML, so the engine's own name is
    // {contentId}.html for both a full-document and an element-subtree capture (content type text/html either way).
    private const string _captureFileName = "capture.html";

    // Serialises the current page's full document (doctype + <html>) or, with a `selector`, that element's subtree
    // (its outerHTML) and streams the UTF-8 bytes to the `to` sink — content-addressed and idempotent exactly like
    // download. The captured document NEVER routes through the credential scrubber (customer content → customer storage);
    // only the resulting blob ref (a hash) is bound and traced. Binds cap = { contentId, sha256, sizeBytes, storedAs, stored }.
    private async ValueTask CaptureAsync(JsonElement body, CancellationToken ct)
    {
        var sink = ResolveSink(await ExprAsync(body, "to", ct), InterpreterErrorCodes.InvalidCaptureTarget, InterpreterErrorCodes.UnknownCaptureSink, "capture");
        var html = body.TryGetProperty("selector", out _)
            ? await (await ResolveSelectorAsync(body, ct)).OuterHTMLAsync(ct) // the element's subtree: outerHTML (the element itself + descendants), not innerHTML
            : await _scope.PageHandle.ContentAsync(ct);                       // the full serialised document: doctype + <html>, not innerHtml('html')

        var artifact = await StoreArtifactAsync(sink, CaptureBytesWithinCap(html), _captureFileName, ct);
        _scope.Set(NodeJson.RequireString(body, "var"), artifact.Binding);

        // Captured: metadata only (blob ref + size + hash) — the HTML streamed straight to the sink, never into the event.
        await StepAsync(new Captured(artifact.StoredAs, artifact.SizeBytes, artifact.Sha256, _clock.GetUtcNow()), ct);
    }

    // Encodes the serialised document to UTF-8 and enforces the run-wide captured-bytes cap on the cumulative total
    // (across every capture in the run) — the sibling of the download cap. Unlike a streamed download, a serialised
    // document is already materialised whole by the backend, so the cap is checked once here, before the upload.
    private byte[] CaptureBytesWithinCap(string html)
    {
        var data = Encoding.UTF8.GetBytes(html);
        _capturedBytes += data.Length;
        if (_capturedBytes > _limits.MaxCapturedBytes)
        {
            throw new InterpreterException(
                InterpreterErrorCodes.MaxCaptureBytesExceeded, $"run exceeded its {_limits.MaxCapturedBytes}-byte capture cap");
        }

        return data;
    }

    // Hashes the bytes to the engine-native content identity and streams them to the sink idempotently: an
    // already-present blob short-circuits to stored:true WITHOUT re-uploading (else the sink's own success — false ⇒ a
    // handling reject). Both calls carry the run's tenant so the sink partitions storage and probes existence per tenant.
    // Shared by download and capture so the two BYO-storage channels compute identity and dedup byte-for-byte identically.
    private async ValueTask<StoredArtifact> StoreArtifactAsync(IDownloadSink sink, byte[] data, string? suggestedFilename, CancellationToken ct)
    {
        var hash = SHA256.HashData(data);
        var contentId = AttachmentContentId.FromHash(hash);
        var sha256 = Convert.ToHexStringLower(hash);
        var storedAs = AttachmentContentId.BuildStoredName(contentId, suggestedFilename);
        long sizeBytes = data.Length;

        var stored = await sink.ExistsAsync(_tenant, contentId, ct)
            || await sink.StoreAsync(_tenant, new StoredDownload(contentId, storedAs, sizeBytes, sha256), new MemoryStream(data, writable: false), ct);

        var binding = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contentId"] = contentId.ToString(),
            ["sha256"] = sha256,
            ["sizeBytes"] = sizeBytes,
            ["storedAs"] = storedAs,
            ["stored"] = stored,
        };
        return new StoredArtifact(storedAs, sizeBytes, sha256, binding);
    }

    // The engine-side result of storing one artifact: the trace-event metadata plus the { … } map bound to the node's var.
    private sealed record StoredArtifact(string StoredAs, long SizeBytes, string Sha256, Dictionary<string, object?> Binding);

    // Drains the download to bytes while enforcing the run-wide downloaded-bytes cap AS THE BYTES FLOW: each chunk
    // advances the cumulative total (across every download in the run), and an over-cap total aborts mid-stream — never
    // after buffering a huge body. The bytes are still materialised whole for the SHA-256 content identity.
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

    // Resolves a storageTarget Expr value to its registered sink, surfacing the caller node's own failure slugs
    // (download vs capture) so a target/kind fault names the node that raised it. Shared by download, capture, and
    // config.captureOnFailure — the one storageTarget → IDownloadSink resolution the whole BYO-storage surface uses.
    private IDownloadSink ResolveSink(object? target, string invalidCode, string unknownCode, string nodeLabel)
    {
        if (target is not Dictionary<string, object?> map)
        {
            throw new InterpreterException(invalidCode, $"{nodeLabel} 'to' must resolve to a storageTarget {{ kind, name }} object");
        }

        var kind = map.GetValueOrDefault("kind") as string
            ?? throw new InterpreterException(invalidCode, "a storageTarget requires a string 'kind'");

        return _sinks.TryResolve(kind, out var sink)
            ? sink
            : throw new InterpreterException(unknownCode, $"no download sink is registered for kind '{kind}'");
    }

    // ----- state + control flow ---------------------------------------------

    private async ValueTask SetAsync(JsonElement body, CancellationToken ct)
    {
        var varName = NodeJson.RequireString(body, "var");
        var value = await ExprAsync(body, "value", ct);
        if (NodeJson.OptionalString(body, "path") is { } setPath)
        {
            await SetPathAsync(varName, setPath, value, ct);
        }
        else
        {
            _scope.Set(varName, value);
        }

        await StepAsync(new Extracted(varName, ValueRef(value), _clock.GetUtcNow()), ct); // key + shape ref only, never the value
    }

    // set with a `path` mutates INSIDE an existing map var. The var and every intermediate segment must be a
    // map (else terminal type_error); the leaf key is upserted (add-or-overwrite, not Dictionary.Add's throw-on-exists).
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
        var into = NodeJson.RequireString(body, "into");
        var value = await ExprAsync(body, "value", ct);
        _scope.Push(into, value);
        await StepAsync(new Extracted(into, ValueRef(value), _clock.GetUtcNow()), ct); // list name + pushed-item shape ref
    }

    private async ValueTask LogAsync(JsonElement body, CancellationToken ct)
    {
        var level = NodeJson.RequireString(body, "level");
        var message = await CrawldadTemplate.Parse(NodeJson.RequireString(body, "message")).RenderAsync(_scope, ct);
        await EmitAsync(new LogEmitted(level, message, _clock.GetUtcNow()), ct);
    }

    private async ValueTask GuardAsync(JsonElement body, CancellationToken ct)
    {
        if (ExpressionValues.RequireBool(await ExprAsync(body, "cond", ct)))
        {
            return; // the condition held — nothing to do
        }

        await RaiseFailureAsync(NodeJson.RequireObject(body, "elseFail"), ct);
    }

    private ValueTask FailAsync(JsonElement body, CancellationToken ct) => RaiseFailureAsync(body, ct);

    // Builds and throws the typed failure from a Failure payload, rendering its message template at raise time.
    private async ValueTask RaiseFailureAsync(JsonElement failure, CancellationToken ct)
    {
        var failureClass = NodeJson.RequireString(failure, "class");
        var code = NodeJson.RequireString(failure, "code");
        var message = await CrawldadTemplate.Parse(NodeJson.RequireString(failure, "message")).RenderAsync(_scope, ct);
        throw new CrawldadFailureException(failureClass, code, message);
    }

    private async ValueTask<Flow> IfAsync(JsonElement body, CancellationToken ct)
    {
        if (ExpressionValues.RequireBool(await ExprAsync(body, "cond", ct)))
        {
            return await ExecuteBlockAsync(NodeJson.RequireElement(body, "then"), ct);
        }

        return body.TryGetProperty("else", out var elseBlock)
            ? await ExecuteBlockAsync(elseBlock, ct)
            : Flow.Normal;
    }

    // switch: first true `when` wins; its Flow (a break/continue) propagates like `if`. No default + no match = no-op.
    private async ValueTask<Flow> SwitchAsync(JsonElement body, CancellationToken ct)
    {
        foreach (var branch in NodeJson.RequireElement(body, "cases").EnumerateArray())
        {
            if (ExpressionValues.RequireBool(await ExprAsync(branch, "when", ct)))
            {
                return await ExecuteBlockAsync(NodeJson.RequireElement(branch, "do"), ct);
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
        var forSpec = NodeJson.RequireObject(body, "for");
        var varName = NodeJson.RequireString(forSpec, "var");
        var toExpr = NodeJson.RequireElement(forSpec, "to");
        var inclusive = NodeJson.OptionalBool(forSpec, "inclusiveTo", false);
        var step = forSpec.TryGetProperty("step", out var s) ? RequireIntegralBound(await BoundAsync(s, ct), "step") : 1L;
        var doBlock = NodeJson.RequireElement(body, "do");

        var i = RequireIntegralBound(await BoundAsync(NodeJson.RequireElement(forSpec, "from"), ct), "from");
        var iterations = 0L;
        using var shadow = _scope.Shadow((varName, (object?)i));

        while (true)
        {
            _scope.Set(varName, i);
            var to = RequireIntegralBound(await BoundAsync(toExpr, ct), "to"); // re-evaluated each iteration, matching the reference's condition
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

    // while: do-while — BODY FIRST, then test (matching the reference's do…while).
    private async ValueTask<Flow> WhileLoopAsync(JsonElement body, JsonElement whileExpr, CancellationToken ct)
    {
        var max = ReadMaxIterations(body);
        var doBlock = NodeJson.RequireElement(body, "do");
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
        var asName = NodeJson.RequireString(body, "as");
        var indexName = NodeJson.OptionalString(body, "index");
        var doBlock = NodeJson.RequireElement(body, "do");
        var source = await ExprAsync(body, "in", ct);

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
        await _scope.Sel.ResolveNodeAsync(NodeJson.RequireElement(body, "selector"), FrameArg(body), ct);

    // Resolves a node's `in:` (a frame var name) to a bound frame handle, or null when absent (page-rooted).
    private IFrameHandle? FrameArg(JsonElement body) =>
        NodeJson.OptionalString(body, "in") is { } inVar ? _scope.Sel.RequireFrame(inVar) : null;

    // A required expression field on a node body: a missing/non-string field is a classified malformed_node, never a
    // raw GetString throw. The two-arg overload takes an already-extracted value (an optional field, or the while test).
    private ValueTask<object?> ExprAsync(JsonElement body, string field, CancellationToken ct) =>
        CrawldadExpression.Parse(NodeJson.RequireString(body, field)).EvaluateAsync(_scope, ct);

    private ValueTask<object?> ExprAsync(JsonElement expr, CancellationToken ct) =>
        CrawldadExpression.Parse(NodeJson.RequireStringValue(expr, "expression")).EvaluateAsync(_scope, ct);

    // A loop-for bound (from/to/step) is either an Expr string or a typed JSON number literal, evaluated through the
    // very same expression parser (a number is parsed from its raw text) — so a JSON number N behaves exactly as the
    // Expr "N". The two forms are fully interchangeable, including a non-advancing typed 0 caught by maxIterations.
    private ValueTask<object?> BoundAsync(JsonElement bound, CancellationToken ct) =>
        CrawldadExpression.Parse(bound.ValueKind == JsonValueKind.String ? bound.GetString()! : bound.GetRawText())
            .EvaluateAsync(_scope, ct);

    // A loop.for bound must evaluate to an integer the long loop counter can take: a long, or a double with no
    // fractional part — coerced exactly as ExpressionValues.RequireIndex coerces an array index. A non-integral or
    // non-number bound is a terminal type_error, never the raw (long) cast that used to escape as an unhandled 500.
    private static long RequireIntegralBound(object? value, string boundName) => value switch
    {
        long l => l,
        double d when !double.IsInfinity(d) && d == Math.Floor(d) => (long)d,
        double => throw ExpressionValues.TypeError(
            $"loop.for bound '{boundName}' must be an integer, got {ExpressionValues.ToStringValue(value)}"),
        _ => throw ExpressionValues.TypeError(
            $"loop.for bound '{boundName}' must be an integer, got {ExpressionValues.TypeName(value)}"),
    };

    // maxIterations is present by the structural pre-pass (loop/forEach require it), but its KIND is unchecked on an
    // inline payload — a non-integer classifies as malformed_node here rather than throwing from a raw GetInt64.
    private static long ReadMaxIterations(JsonElement body) =>
        NodeJson.RequireLongValue(body.GetProperty("maxIterations"), "maxIterations");

    private static string? OptString(JsonElement body, string field) => NodeJson.OptionalString(body, field);

    private int Timeout(JsonElement body) => NodeJson.OptionalInt(body, "timeoutMs", _defaultTimeoutMs);

    private RunStats Stats(DateTimeOffset startedAt) =>
        new((long)(_clock.GetUtcNow() - startedAt).TotalMilliseconds, _steps, _requests, _session?.CacheHits ?? 0, _downloads, _selectorMisses);

    // Records one selector miss reported by an extraction builtin (the ISelectorMissSink seam). Always counts it into
    // stats.selectorMisses (the soft signal, present on both the sync and durable paths); on the FIRST miss of this exact
    // selector emits a SelectorMiss trace event (durable path only, budget-counted like other step events — dedupe keeps
    // a per-row drift from ever flooding the stream). Returns true when the miss must be terminal — the extraction was
    // require()-wrapped (`required`) or config.strictExtraction promotes every miss — so the builtin raises selector_miss.
    private async ValueTask<bool> RecordSelectorMissAsync(string selector, bool required, CancellationToken ct)
    {
        _selectorMisses++;
        if (_seenMissSelectors.Add(selector))
        {
            await StepAsync(new SelectorMiss(selector, _currentStepIndex, _clock.GetUtcNow()), ct);
        }

        return required || _strictExtraction;
    }

    // The ISelectorMissSink the run scope hands the extraction builtins: a thin adapter so the miss recording reads the
    // interpreter's LIVE state (current step, strict flag, counter) rather than a snapshot captured at scope-build time.
    private sealed class SelectorMissReporter(RunInterpreter owner) : ISelectorMissSink
    {
        public ValueTask<bool> RecordAsync(string selector, bool required, CancellationToken ct) =>
            owner.RecordSelectorMissAsync(selector, required, ct);
    }

    private RunOutcome Failed(string failureClass, string code, string message, DateTimeOffset startedAt) =>
        new(RunStatus.Failed, null, new RunFailureDetail(failureClass, code, message, new RunStepRef(_currentStepIndex, _currentKind)), null, Stats(startedAt), _events);

    // ----- trace emission + screenshot-on-failure ----------------------

    // Emits a coarse trace event (LogEmitted/RunAttemptFailed): live through the observer on the durable path (in occurrence
    // order), else accumulated for the synchronous endpoint to append at the end (behaviour + goldens unchanged).
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
    // synchronous path no-ops, so its stream — and every golden — is byte-identical to before (and counts no event).
    private ValueTask StepAsync(object traceEvent, CancellationToken ct)
    {
        if (_observer is null)
        {
            return ValueTask.CompletedTask;
        }

        EnforceEventBudget();
        return _observer.EmitAsync(traceEvent, ct);
    }

    // Max-events cap: counts every trace event the interpreter appends to the run stream (step + coarse +
    // checkpoint markers) and trips terminally past the cap. The terminal RunFailed/RunSucceeded is appended by the
    // executor/endpoint after the interpreter returns, so it always lands — the cap bounds the run's own emitted volume.
    private void EnforceEventBudget()
    {
        if (++_eventCount > _limits.MaxEvents)
        {
            throw new InterpreterException(InterpreterErrorCodes.MaxEventsExceeded, $"run exceeded its {_limits.MaxEvents}-event cap");
        }
    }

    // Reports a failure: captures a screenshot when asked (a page is bound), emits StepFailed with its ref, then
    // builds the RunOutcome. On the synchronous path StepAsync no-ops and the screenshot is skipped — Failed() as before.
    private async Task<RunOutcome> ReportFailedAsync(string failureClass, string code, string message, DateTimeOffset startedAt, bool screenshot, CancellationToken ct)
    {
        // `screenshot` is set only when a page is bound (the retry/exhaustion path), which is exactly the precondition
        // both failure artifacts need — so the failing page's HTML lands next to its screenshot when both are enabled.
        var screenshotRef = screenshot ? await CaptureFailureScreenshotAsync(ct) : null;
        var captureRef = screenshot ? await CaptureFailureHtmlAsync(ct) : null;

        // The terminal StepFailed marker bypasses the event budget (like the RunFailed the executor appends after): a
        // max_events_exceeded failure must still be able to report itself, not re-trip the cap while emitting its own marker.
        // It carries both failure-artifact refs — the screenshot and the captureOnFailure HTML doc — so the failure links
        // each explicitly (the capture ref matches its captures[] entry, byte-exact once the shared scrubber runs on both).
        await EmitTerminalStepAsync(new StepFailed(_currentStepIndex, code, screenshotRef, captureRef, _clock.GetUtcNow()), ct);
        return Failed(failureClass, code, message, startedAt);
    }

    // Emits the terminal StepFailed marker straight to the observer (durable path only), never counting it against the
    // event budget — the failure's own marker always lands, exactly as the terminal RunFailed does.
    private ValueTask EmitTerminalStepAsync(object traceEvent, CancellationToken ct) =>
        _observer is null ? ValueTask.CompletedTask : _observer.EmitAsync(traceEvent, ct);

    // Captures the failing page to blob storage and returns the ref, or null when unavailable: no observer (sync
    // path), no store, disabled via config, or no session/page bound. Best-effort — a crashed page's capture failure is
    // tolerated so a screenshot never masks the run's own failure.
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

    // Captures the failing page's full HTML to the config.captureOnFailure BYO sink and records a Captured event with
    // only its ref — the diagnostic companion to the failure screenshot (selector drift is easiest to read off the DOM).
    // Returns the captured document's content-addressed ref so the StepFailed marker can link it explicitly, or null when
    // nothing was captured. Durable path only: no observer (sync path) or no configured sink ⇒ nothing to do, like the
    // failure screenshot. Reached only from the retry/exhaustion failure path (a page is bound), so the sink being set ⇒
    // a page to serialise. The failing page is exempt from the capture byte cap (one diagnostic page is not a runaway),
    // and a crashed page's serialisation failure is tolerated so a failed capture never masks the run's own failure.
    private async ValueTask<string?> CaptureFailureHtmlAsync(CancellationToken ct)
    {
        if (_observer is null || _captureOnFailureSink is null)
        {
            return null;
        }

        try
        {
            var data = Encoding.UTF8.GetBytes(await _scope.PageHandle.ContentAsync(ct));
            var artifact = await StoreArtifactAsync(_captureOnFailureSink, data, _captureFileName, ct);
            // Budget-exempt like the terminal StepFailed marker: a run that failed on max_events must still bank its failing page.
            await EmitTerminalStepAsync(new Captured(artifact.StoredAs, artifact.SizeBytes, artifact.Sha256, _clock.GetUtcNow()), ct);
            return artifact.StoredAs; // the same ref its Captured twin carries — StepFailed links the failing page's doc by it
        }
        catch (BrowserException)
        {
            // a crashed/torn-down page can fail to serialise — tolerate it (best-effort, like the failure screenshot)
            return null;
        }
    }

    private static string HeadKind(JsonElement node) => node.EnumerateObject().First().Name;

    private long ElapsedMs(DateTimeOffset start) => (long)(_clock.GetUtcNow() - start).TotalMilliseconds;

    // The click node's declared selector text for the Clicked event: a string selector's raw text, or a structured
    // Sel's raw JSON. The un-rendered form (never the matched element), scrubbed defensively at the sink.
    private static string SelectorLabel(JsonElement body)
    {
        var selector = body.GetProperty("selector");
        return selector.ValueKind == JsonValueKind.String ? selector.GetString()! : selector.GetRawText();
    }

    // A PII-safe shape descriptor of a bound value for the Extracted event: kind + size, NEVER the value.
    private static string ValueRef(object? value) => value switch
    {
        null => "null",
        string s => $"string({s.Length})",
        List<object?> list => $"list({list.Count})",
        IReadOnlyDictionary<string, object?> map => $"map({map.Count})",
        _ => "scalar", // bool / number
    };

    // screenshot-on-failure toggle: captured by default, suppressed by config.screenshotOnFailure:false.
    private static bool ReadScreenshotOnFailure(JsonElement config) =>
        NodeJson.OptionalBool(config, "screenshotOnFailure", true);

    // strict-extraction toggle: soft by default (a selector miss only counts + emits), made terminal for every
    // extraction by config.strictExtraction:true — the require()-wrapper severity applied as the default.
    private static bool ReadStrictExtraction(JsonElement config) =>
        NodeJson.OptionalBool(config, "strictExtraction", false);

    // A cooperative cancel stopped the run between steps. Salvage a partial result (the payload's `result` over the
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

    // ----- checkpoint + resume -----------------------------------------

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
            await ExecuteBlockAsync(NodeJson.RequireElement(body, "resume"), ct); // re-navigate to the restored cursor (bound to `checkpoint`)
        }

        var name = NodeJson.RequireString(body, "name");
        var cursor = JsonValues.ToJson(await ExprAsync(body, "cursor", ct));
        EnforceEventBudget(); // the checkpoint marker is a stream event too
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

    // Resumes execution from the checkpoint the executor restored: re-seed the accumulated vars, bind the cursor to
    // the `checkpoint` var for the resume sub-program, then re-enter at the checkpoint's enclosing top-level step. The
    // first checkpoint node re-navigates (CheckpointAsync's _resumePending path); earlier steps are skipped, never refetched.
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
                await StepAsync(new StepStarted(index, HeadKind(step), _clock.GetUtcNow()), ct); // marker for each resumed step
                await ExecuteNodeAsync(step, ct);
            }

            index++;
        }
    }
}
