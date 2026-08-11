using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;

namespace Crawldad.Tests.Unit;

/// <summary>The connect boundary's bounded retry (<c>config.connectRetry</c>): a transient connect fault is retried with
/// a fresh <c>ConnectAsync</c> — re-resolving the credentialRef so a connector's mid-window re-registration is picked up
/// — while an auth-shaped fault fails fast; exhaustion stays a terminal <c>backend_unavailable</c> whose message counts
/// the attempts. Distinct from <c>config.retry</c>, which never reaches the connect. Delays run at 0 ms under the frozen
/// clock (one real-clock case proves the delay path), exactly like the program-retry suite.</summary>
public class ConnectRetryTests
{
    private const string _tenant = TestTenants.InterpreterTenant;

    // A payload whose backend resolves from input, with an optional connectRetry block spliced into config.
    private static string Payload(string? connectRetry = null) =>
        $$"""
        { "name": "t", "config": { "backend": "input.backend"{{(connectRetry is null ? "" : $", \"connectRetry\": {connectRetry}")}} },
          "steps": [], "result": "null" }
        """;

    private const string _inputs = """{ "backend": { "adapter": "flaky", "credentialRef": "tunnel" } }""";

    private static async Task<RunOutcome> RunAsync(IBrowserBackend backend, string? connectRetry = null, TimeProvider? clock = null, CancellationToken ct = default)
    {
        using var payloadDoc = JsonDocument.Parse(Payload(connectRetry));
        using var inputsDoc = JsonDocument.Parse(_inputs);
        var input = (Dictionary<string, object?>)JsonValues.FromJson(inputsDoc.RootElement)!;
        var interpreter = new RunInterpreter(
            payloadDoc.RootElement.Clone(), input, new StubRegistry(backend), new NoSinks(), clock ?? new FakeClock(), _tenant);
        return await interpreter.RunAsync(ct);
    }

    // ----- the two acceptance cases ------------------------------------------

    [Fact]
    public async Task A_run_in_a_tunnel_reconnect_window_succeeds_on_attempt_2_with_the_freshly_reread_secret()
    {
        // The connector rotated the tunnel URL under a stable name mid-window: attempt 1 hits the dying tunnel (a
        // transient fault), attempt 2 re-resolves the credentialRef and gets the fresh URL, and connects. (delayMs 0 so
        // the frozen clock does not hang; the real-clock delay path is proven separately below.)
        var resolver = new RotatingResolver("stale-tunnel-url", "fresh-tunnel-url");
        var backend = new ResolvingConnectBackend(resolver, failuresBeforeSuccess: 1);

        var outcome = await RunAsync(backend, """{ "maxAttempts": 3, "delayMs": 0 }""");

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        backend.Resolved.ShouldBe(["stale-tunnel-url", "fresh-tunnel-url"]); // attempt 2 re-read the rotated secret
        outcome.Events.OfType<RunConnectAttemptFailed>().Select(e => e.Attempt).ShouldBe([1]); // one retried attempt, before success
        outcome.Events.OfType<RunConnectAttemptFailed>().ShouldAllBe(e => e.Code == "backend_unavailable");
    }

    [Fact]
    public async Task A_run_against_a_deleted_credential_fails_fast_with_no_retry()
    {
        // An absent/deleted credential surfaces as a NON-retryable connect fault (the adapter classifies its
        // SecretNotFoundException so): even a generous connectRetry must not re-attempt — terminal on the first failure.
        var backend = new CountingConnectBackend(retryable: false, "the credential could not be resolved");

        var outcome = await RunAsync(backend, """{ "maxAttempts": 5, "delayMs": 10000 }""");

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe("backend_unavailable");
        outcome.Failure.Message.ShouldBe("the credential could not be resolved"); // surfaced verbatim, not attempt-annotated
        backend.ConnectAttempts.ShouldBe(1); // single-shot — no retry, no wait
        outcome.Events.OfType<RunConnectAttemptFailed>().ShouldBeEmpty();
    }

    // ----- exhaustion, defaults, bounds, delay, cancellation -----------------

    [Fact]
    public async Task A_transient_fault_that_never_clears_exhausts_to_a_terminal_backend_unavailable()
    {
        var backend = new CountingConnectBackend(retryable: true, "failed to establish a backend session");

        var outcome = await RunAsync(backend, """{ "maxAttempts": 3, "delayMs": 0 }""");

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal"); // a connect fault is terminal backend_unavailable, never retryable-exhausted
        outcome.Failure.Code.ShouldBe("backend_unavailable");
        outcome.Failure.Message.ShouldBe("failed to establish a backend session (after 3 connect attempts)"); // reflects the attempts made
        backend.ConnectAttempts.ShouldBe(3);
        outcome.Events.OfType<RunConnectAttemptFailed>().Select(e => e.Attempt).ShouldBe([1, 2]); // attempts 1,2 retried; 3 exhausted
    }

    [Fact]
    public async Task Absent_connect_retry_keeps_the_connect_single_shot_even_for_a_transient_fault()
    {
        // Default (no connectRetry): behaviour is unchanged from before the feature — one connect attempt, terminal.
        var backend = new CountingConnectBackend(retryable: true, "transient blip");

        var outcome = await RunAsync(backend, connectRetry: null);

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe("backend_unavailable");
        backend.ConnectAttempts.ShouldBe(1);
        outcome.Events.OfType<RunConnectAttemptFailed>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Max_attempts_is_clamped_to_its_cap()
    {
        // An absurd maxAttempts is clamped, not honoured — the connect holds a backend slot across real waits.
        var backend = new CountingConnectBackend(retryable: true, "blip");

        await RunAsync(backend, """{ "maxAttempts": 999, "delayMs": 0 }""");

        backend.ConnectAttempts.ShouldBe(10); // ConnectRetryPolicy.MaxAttemptsCap
    }

    [Fact]
    public async Task Max_attempts_below_the_floor_is_clamped_to_one()
    {
        var backend = new CountingConnectBackend(retryable: true, "blip");

        await RunAsync(backend, """{ "maxAttempts": 0, "delayMs": 0 }""");

        backend.ConnectAttempts.ShouldBe(1); // clamped up to the single-attempt floor
    }

    [Fact]
    public async Task The_backoff_runs_through_the_injected_time_provider()
    {
        // A real (tiny) delay exercises the Task.Delay path without a frozen-clock hang, mirroring the program-retry suite.
        var backend = new ResolvingConnectBackend(new RotatingResolver("t"), failuresBeforeSuccess: 1);

        var outcome = await RunAsync(backend, """{ "maxAttempts": 3, "delayMs": 1 }""", clock: TimeProvider.System);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
    }

    [Fact]
    public async Task A_cancelled_run_stops_during_the_backoff_wait()
    {
        // Run cancellation/deadline is honoured mid-backoff: the wait throws and the connect is not re-attempted.
        using var cts = new CancellationTokenSource();
        var backend = new CancelOnFirstConnectBackend(cts);

        await Should.ThrowAsync<OperationCanceledException>(
            () => RunAsync(backend, """{ "maxAttempts": 3, "delayMs": 60000 }""", TimeProvider.System, cts.Token));

        backend.ConnectAttempts.ShouldBe(1); // cancelled during the backoff — never re-entered the connect
    }

    // ----- scripted backends + doubles ---------------------------------------

    // Throws a connect fault (retryable or not) on every attempt, counting the attempts.
    private sealed class CountingConnectBackend(bool retryable, string message) : IBrowserBackend
    {
        public int ConnectAttempts { get; private set; }

        public Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
        {
            ConnectAttempts++;
            throw new BrowserConnectException(message, retryable);
        }
    }

    // Re-resolves the credentialRef each attempt (as a real adapter does), records what it saw, throws a transient fault
    // until the scripted success attempt, then hands back a fake session so the run can complete.
    private sealed class ResolvingConnectBackend(IConnectCredentialResolver resolver, int failuresBeforeSuccess) : IBrowserBackend
    {
        private readonly FakeBrowserBackend _inner = new(Runner.FixturesRoot);

        public List<string> Resolved { get; } = [];

        public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
        {
            Resolved.Add(await resolver.ResolveConnectAsync(binding.CredentialRef!, binding.Tenant!, ct));
            if (Resolved.Count <= failuresBeforeSuccess)
            {
                throw new BrowserConnectException("simulated tunnel reconnect window", retryable: true);
            }

            return await _inner.ConnectAsync(Runner.FakeBinding("caphome-search"), policy, ct);
        }
    }

    // Throws a transient fault and cancels the run token as it does, so the following backoff wait observes cancellation.
    private sealed class CancelOnFirstConnectBackend(CancellationTokenSource cts) : IBrowserBackend
    {
        public int ConnectAttempts { get; private set; }

        public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
        {
            ConnectAttempts++;
            await cts.CancelAsync(); // cancel the run mid-connect, so the following backoff wait observes it
            throw new BrowserConnectException("transient — but the run is being cancelled", retryable: true);
        }
    }

    // Returns a different secret per call (then sticks on the last), so a test can prove attempt N re-read a rotated value.
    private sealed class RotatingResolver(params string[] values) : IConnectCredentialResolver
    {
        private int _index;

        public Task<string> ResolveConnectAsync(string credentialRef, string tenant, CancellationToken ct) =>
            Task.FromResult(values[Math.Min(_index++, values.Length - 1)]);
    }

    private sealed class StubRegistry(IBrowserBackend backend) : IBrowserBackendRegistry
    {
        public bool TryResolve(string adapter, [NotNullWhen(true)] out IBrowserBackend? resolved)
        {
            resolved = backend;
            return true;
        }
    }

    private sealed class NoSinks : IDownloadSinkRegistry
    {
        public bool TryResolve(string kind, [NotNullWhen(true)] out IDownloadSink? sink)
        {
            sink = null;
            return false;
        }
    }
}
