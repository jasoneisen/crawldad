using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Storage;

namespace Crawldad.Tests.Unit;

/// <summary>The interpreter's handling of a real-adapter connect failure: a <see cref="BrowserConnectException"/>
/// from <c>ConnectAsync</c> is a terminal <c>backend_unavailable</c>, classified like the fake's setup fault, and
/// its (already secret-free) message is surfaced verbatim — and the connect is single-shot (never retried).</summary>
public class InterpreterBackendTests
{
    [Fact]
    public async Task A_connect_failure_is_a_terminal_backend_unavailable()
    {
        const string Payload =
            """{ "name": "t", "config": { "backend": "input.backend" }, "steps": [], "result": "null" }""";
        const string Inputs = """{ "backend": { "adapter": "flaky" } }""";

        using var payloadDoc = JsonDocument.Parse(Payload);
        using var inputsDoc = JsonDocument.Parse(Inputs);
        var input = (Dictionary<string, object?>)JsonValues.FromJson(inputsDoc.RootElement)!;

        var interpreter = new RunInterpreter(
            payloadDoc.RootElement.Clone(),
            input,
            new StubRegistry(new ThrowingBackend()),
            new NoSinks(),
            new FakeClock(),
            TestTenants.InterpreterTenant);

        var outcome = await interpreter.RunAsync(CancellationToken.None);

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe("backend_unavailable");
        outcome.Failure.Message.ShouldBe("simulated remote outage");
        outcome.Stats.CacheHits.ShouldBe(0); // no session was ever established
    }

    [Fact]
    public async Task A_connect_failure_is_not_retried_even_under_a_generous_retry_policy()
    {
        // config.retry wraps only the POST-connect program; the connect itself is single-shot and outside the retry
        // loop. A generous policy (and even listing backend_unavailable in retryOn) must NOT re-attempt the connect —
        // the run is a terminal backend_unavailable on the first failure, with the connect tried exactly once.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend",
              "retry": { "maxAttempts": 5, "delayMs": 0, "backoff": "constant", "retryOn": ["timeout","pageCrashed","backend_unavailable"] } },
              "steps": [], "result": "null" }
            """;
        const string Inputs = """{ "backend": { "adapter": "flaky" } }""";

        using var payloadDoc = JsonDocument.Parse(Payload);
        using var inputsDoc = JsonDocument.Parse(Inputs);
        var input = (Dictionary<string, object?>)JsonValues.FromJson(inputsDoc.RootElement)!;
        var backend = new CountingConnectBackend();

        var interpreter = new RunInterpreter(
            payloadDoc.RootElement.Clone(),
            input,
            new StubRegistry(backend),
            new NoSinks(),
            new FakeClock(),
            TestTenants.InterpreterTenant);

        var outcome = await interpreter.RunAsync(CancellationToken.None);

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal"); // terminal, NOT retryable-exhausted
        outcome.Failure.Code.ShouldBe("backend_unavailable");
        backend.ConnectAttempts.ShouldBe(1); // single-shot connect — the retry loop never re-enters it
        outcome.Events.OfType<RunAttemptFailed>().ShouldBeEmpty(); // no attempt was ever retried
    }

    private sealed class ThrowingBackend : IBrowserBackend
    {
        public Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct) =>
            throw new BrowserConnectException("simulated remote outage");
    }

    private sealed class CountingConnectBackend : IBrowserBackend
    {
        public int ConnectAttempts { get; private set; }

        public Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
        {
            ConnectAttempts++;
            throw new BrowserConnectException("simulated tunnel outage");
        }
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
