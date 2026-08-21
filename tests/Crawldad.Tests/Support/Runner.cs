using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Crawldad.Web.Features.Fixtures;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;

namespace Crawldad.Tests.Support;

/// <summary>White-box harness for the interpreter/fake unit tests: builds a <see cref="RunInterpreter"/> over the
/// record/replay fake and shipped fixtures, exposing the fake page + a bound <see cref="RunScope"/> for scope/selector
/// tests. The Alba integration tests drive the same code through real HTTP.</summary>
internal static class Runner
{
    /// <summary>The fixtures root in the test output (Crawldad.Web ships these as copied content).</summary>
    public static string FixturesRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>The default inputs binding the fake backend to the caphome-search fixture.</summary>
    public const string FakeInputs = """{ "backend": { "adapter": "fake", "options": { "fixture": "caphome-search" } } }""";

    /// <summary>The acceptance fragment payload (JSON), read from the test output.</summary>
    public static string FragmentPayload() => File.ReadAllText(Path.Combine(FixturesRoot, "Payloads", "search-fragment.json"));

    /// <summary>The golden result body (JSON), read from the shipped fixture.</summary>
    public static string Golden() => File.ReadAllText(Path.Combine(FixturesRoot, "caphome-search", "golden.json"));

    /// <summary>Runs a full payload against the fake backend and returns the outcome.</summary>
    /// <param name="inputsJson">The inputs JSON (defaults to the fake caphome-search binding).</param>
    public static async Task<RunOutcome> RunAsync(string payloadJson, string inputsJson = FakeInputs) =>
        (await RunWithFakeAsync(payloadJson, inputsJson)).Outcome;

    /// <summary>Runs a payload and also hands back the fake backend, so a test can inspect the final (mutated) DOM.</summary>
    /// <param name="clock">Defaults to the frozen <see cref="FakeClock"/>; pass a real one to exercise retry delays.</param>
    /// <param name="secretStores">Null (the default) makes a <c>fill.secret</c> terminal.</param>
    public static async Task<(RunOutcome Outcome, FakeBrowserBackend Backend)> RunWithFakeAsync(
        string payloadJson, string inputsJson = FakeInputs, TimeProvider? clock = null, IDownloadSinkRegistry? sinks = null, RunLimits? limits = null,
        ISecretStoreRegistry? secretStores = null, IRunSecretScope? secretScope = null, CancellationToken ct = default)
    {
        using var payloadDoc = JsonDocument.Parse(payloadJson);
        using var inputsDoc = JsonDocument.Parse(inputsJson);
        var input = JsonValues.FromJson(inputsDoc.RootElement) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var backend = new FakeBrowserBackend(FixturesRoot);
        var interpreter = new RunInterpreter(
            payloadDoc.RootElement.Clone(),
            input,
            new SingleBackendRegistry("fake", backend),
            sinks ?? new SingleSinkRegistry("fake", new FakeDownloadSink()),
            clock ?? new FakeClock(),
            TestTenants.InterpreterTenant,
            limits: limits,
            secretStores: secretStores,
            secretScope: secretScope);
        return (await interpreter.RunAsync(ct), backend);
    }

    /// <summary>Runs a payload against a supplied download sink and returns the outcome, so a test can assert the
    /// sink's exists/store call tracking (the content-addressed idempotency contract).</summary>
    /// <param name="sink">Bound under kind <c>"fake"</c> and inspected afterwards.</param>
    public static async Task<RunOutcome> RunWithSinkAsync(string payloadJson, string inputsJson, FakeDownloadSink sink) =>
        (await RunWithFakeAsync(payloadJson, inputsJson, sinks: new SingleSinkRegistry("fake", sink))).Outcome;

    /// <summary>Drives the interpreter on the durable path so a unit test can assert trace events and screenshots without a database.</summary>
    /// <param name="backend">Defaults to the record/replay fake; a decorator can model a page whose screenshot capture fails.</param>
    /// <param name="secretStores">Null (the default) makes a <c>fill.secret</c> terminal.</param>
    public static async Task<(RunOutcome Outcome, RecordingObserver Observer, InMemoryScreenshotStore Screenshots)> RunWithObserverAsync(
        string payloadJson, string inputsJson = FakeInputs, bool cancelRequested = false, FakeDownloadSink? sink = null, IBrowserBackend? backend = null,
        RunLimits? limits = null, ResumeState? resume = null, ISecretStoreRegistry? secretStores = null, IRunSecretScope? secretScope = null)
    {
        using var payloadDoc = JsonDocument.Parse(payloadJson);
        using var inputsDoc = JsonDocument.Parse(inputsJson);
        var input = JsonValues.FromJson(inputsDoc.RootElement) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var observer = new RecordingObserver { Cancel = cancelRequested };
        var screenshots = new InMemoryScreenshotStore();
        var interpreter = new RunInterpreter(
            payloadDoc.RootElement.Clone(),
            input,
            new SingleBackendRegistry("fake", backend ?? new FakeBrowserBackend(FixturesRoot)),
            new SingleSinkRegistry("fake", sink ?? new FakeDownloadSink()),
            new FakeClock(),
            TestTenants.InterpreterTenant,
            observer,
            resume,
            screenshots,
            limits,
            secretStores,
            secretScope);
        return (await interpreter.RunAsync(CancellationToken.None), observer, screenshots);
    }

    /// <summary>Drives a record-mode interpreter over the fake backend, banking the run into <paramref name="recorder"/> —
    /// the white-box path for the recorder's own branches (caps, dedup, unrecordable ops). Returns the outcome.</summary>
    public static async Task<RunOutcome> RunWithRecorderAsync(string payloadJson, string inputsJson, FixtureRecorder recorder)
    {
        using var payloadDoc = JsonDocument.Parse(payloadJson);
        using var inputsDoc = JsonDocument.Parse(inputsJson);
        var input = JsonValues.FromJson(inputsDoc.RootElement) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var interpreter = new RunInterpreter(
            payloadDoc.RootElement.Clone(),
            input,
            new SingleBackendRegistry("fake", new FakeBrowserBackend(FixturesRoot)),
            new SingleSinkRegistry("fake", new FakeDownloadSink()),
            new FakeClock(),
            TestTenants.InterpreterTenant,
            recorder: recorder);
        return await interpreter.RunAsync(CancellationToken.None);
    }

    /// <summary>A registry with only the fake adapter over the test fixtures root.</summary>
    /// <param name="fixturesRoot">Override the fixtures root (defaults to the test output).</param>
    public static IBrowserBackendRegistry FakeRegistry(string? fixturesRoot = null) =>
        new SingleBackendRegistry("fake", new FakeBrowserBackend(fixturesRoot ?? FixturesRoot));

    /// <summary>Connects the fake backend to a fixture and opens a page.</summary>
    /// <param name="fixture">The fixture directory name.</param>
    public static async Task<FakePageHandle> FakePageAsync(string fixture = "caphome-search")
    {
        var backend = new FakeBrowserBackend(FixturesRoot);
        var session = await backend.ConnectAsync(FakeBinding(fixture), SessionPolicy.Default, CancellationToken.None);
        return (FakePageHandle)await session.NewPageAsync(CancellationToken.None);
    }

    /// <summary>A <see cref="RunScope"/> bound to a fresh fake page (for scope/selector unit tests).</summary>
    /// <param name="input">Optional initial <c>input</c> map contents.</param>
    public static async Task<RunScope> ScopeOnFakeAsync(IReadOnlyDictionary<string, object?>? input = null)
    {
        var scope = new RunScope(input ?? new Dictionary<string, object?>(StringComparer.Ordinal));
        scope.Bind(await FakePageAsync());
        return scope;
    }

    /// <summary>A backend binding for the fake adapter naming a fixture directory.</summary>
    /// <param name="fixture">The fixture directory name.</param>
    public static BackendBinding FakeBinding(string fixture) =>
        new("fake", null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["fixture"] = fixture });

    private sealed class SingleBackendRegistry(string adapter, IBrowserBackend backend) : IBrowserBackendRegistry
    {
        public bool TryResolve(string requested, [NotNullWhen(true)] out IBrowserBackend? resolved)
        {
            if (string.Equals(requested, adapter, StringComparison.Ordinal))
            {
                resolved = backend;
                return true;
            }

            resolved = null;
            return false;
        }
    }

    private sealed class SingleSinkRegistry(string kind, IDownloadSink sink) : IDownloadSinkRegistry
    {
        public bool TryResolve(string requested, [NotNullWhen(true)] out IDownloadSink? resolved)
        {
            if (string.Equals(requested, kind, StringComparison.Ordinal))
            {
                resolved = sink;
                return true;
            }

            resolved = null;
            return false;
        }
    }
}
