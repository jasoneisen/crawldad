using System.Text.Json.Nodes;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Browser.Fake;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Unit;

/// <summary>The <c>download</c> gate: runs its trigger, hashes bytes to the engine-native <c>contentId</c>
/// (= <c>AttachmentHashing</c>), streams to the target sink, and is idempotent by content identity — an
/// already-present blob short-circuits to <c>stored:true</c> with no re-upload, assertable via <see cref="FakeDownloadSink"/>.</summary>
public class DownloadNodeTests
{
    private const string _contentId = "18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48";
    private const string _sha256 = "e22edc18626ec6f58ec1648aa28b2f48fc168b6ce9defa3b40344b1eb22f789e";

    private const string _downloadInputs =
        """{ "backend": { "adapter": "fake", "options": { "fixture": "download-sample" } }, "attachmentStore": { "kind": "fake", "name": "attachmentStore" } }""";

    private static string DownloadPayload() =>
        File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "download-fragment.json"));

    [Fact]
    public async Task Download_hashes_streams_and_composes_the_internal_filename()
    {
        var sink = new FakeDownloadSink();
        var outcome = await Runner.RunWithSinkAsync(DownloadPayload(), _downloadInputs, sink);

        outcome.Status.ShouldBe(RunStatus.Succeeded);
        var first = outcome.Result!.Value.GetProperty("attachments")[0];

        first.GetProperty("attachmentId").GetString().ShouldBe(_contentId);   // engine-native, = AttachmentHashing
        first.GetProperty("sha256").GetString().ShouldBe(_sha256);
        first.GetProperty("sizeBytes").GetInt32().ShouldBe(30);
        first.GetProperty("stored").GetBoolean().ShouldBeTrue();

        // internalFilename is composed by the payload from the SCRAPED "Site Photo.jpg" (.jpg); storedAs is the engine's
        // own name from the download's suggested "report.pdf" (.pdf) — same contentId, two different extensions.
        first.GetProperty("internalFilename").GetString().ShouldBe($"{_contentId}.jpg");
        first.GetProperty("storedAs").GetString().ShouldBe($"{_contentId}.pdf");

        outcome.Stats.Downloads.ShouldBe(2);
    }

    [Fact]
    public async Task Re_downloading_the_same_content_short_circuits_without_re_upload()
    {
        var sink = new FakeDownloadSink();
        var outcome = await Runner.RunWithSinkAsync(DownloadPayload(), _downloadInputs, sink);

        // Both downloads bound stored:true, but the sink uploaded exactly ONCE — the second probe found it present.
        outcome.Result!.Value.GetProperty("attachments")[1].GetProperty("stored").GetBoolean().ShouldBeTrue();
        sink.ExistsCalls.ShouldBe(2);
        sink.StoreCalls.ShouldBe(1);
        sink.Stored.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_sink_that_rejects_the_handling_drives_stored_false_and_the_warn_branch()
    {
        var sink = new FakeDownloadSink(failStore: true);
        var outcome = await Runner.RunWithSinkAsync(DownloadPayload(), _downloadInputs, sink);

        outcome.Status.ShouldBe(RunStatus.Succeeded);          // a rejected download is a warning, not a failure
        outcome.Result!.Value.GetProperty("attachments").GetArrayLength().ShouldBe(0);
        sink.StoreCalls.ShouldBe(2);                           // both attempts tried (neither ever present)
        sink.Stored.Count.ShouldBe(0);

        var warnings = outcome.Events.OfType<LogEmitted>().Select(l => (l.Level, l.Message)).ToList();
        warnings.ShouldBe([("warning", "Handling attachment failed: Site Photo.jpg")]);
    }

    [Theory]
    [InlineData("'not a target'", InterpreterErrorCodes.InvalidDownloadTarget)]  // resolves to a non-object
    [InlineData("{ name: 'x' }", InterpreterErrorCodes.InvalidDownloadTarget)]   // object without a kind
    [InlineData("{ kind: 'nope', name: 'x' }", InterpreterErrorCodes.UnknownDownloadSink)] // kind has no sink
    public async Task A_malformed_or_unknown_download_target_is_terminal(string toExpr, string code)
    {
        var outcome = await Runner.RunAsync(DownloadToPayload(toExpr));

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe(code);
    }

    [Fact]
    public async Task A_trigger_that_starts_no_download_times_out()
    {
        var page = await Runner.FakePageAsync("download-sample");

        // The wait is armed, the trigger does nothing (no download transition fires) → the reference's 180 s timeout.
        await Should.ThrowAsync<BrowserTimeoutException>(
            page.RunAndWaitForDownloadAsync(() => Task.CompletedTask, 180000, CancellationToken.None));
    }

    [Fact]
    public void Keyed_sink_registry_resolves_registered_kinds_and_rejects_unknown()
    {
        var sink = new FakeDownloadSink();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IDownloadSink>("fake", sink);
        using var provider = services.BuildServiceProvider();
        var registry = new KeyedDownloadSinkRegistry(provider);

        registry.TryResolve("fake", out var resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(sink);
        registry.TryResolve("nope", out var missing).ShouldBeFalse();
        missing.ShouldBeNull();
    }

    // A minimal one-step payload whose download node's `to` expression is under test; it fails at target resolution
    // before the trigger runs, so an empty trigger suffices. Bound to the default fake backend (caphome-search).
    private static string DownloadToPayload(string toExpr)
    {
        var download = new JsonObject
        {
            ["trigger"] = new JsonArray(),
            ["to"] = toExpr,
            ["var"] = "dl",
        };
        var payload = new JsonObject
        {
            ["name"] = "t",
            ["config"] = new JsonObject { ["backend"] = "input.backend" },
            ["vars"] = new JsonObject(),
            ["steps"] = new JsonArray(new JsonObject { ["download"] = download }),
            ["result"] = "null",
        };
        return payload.ToJsonString();
    }
}
