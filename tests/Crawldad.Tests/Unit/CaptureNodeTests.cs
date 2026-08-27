using System.Text;
using System.Text.Json.Nodes;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>The <c>capture</c> gate: serialises the current document (or an element subtree via <c>selector</c>) and
/// streams the bytes content-addressed to a BYO sink — symmetric with <c>download</c> (idempotent by content identity),
/// binding the REF, never the HTML. The captured document is byte-faithful: it bypasses the credential scrubber, so a
/// credential-shaped <c>token=</c> in the customer's own page survives verbatim (issue #70).</summary>
public class CaptureNodeTests
{
    private const string _captureInputs =
        """{ "backend": { "adapter": "fake", "options": { "fixture": "capture-sample" } }, "captureStore": { "kind": "fake", "name": "captureStore" } }""";

    private static string CapturePayload() =>
        File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "capture-fragment.json"));

    private static Guid Ref(RunOutcome outcome, string field) =>
        Guid.Parse(outcome.Result!.Value.GetProperty("pages")[0].GetProperty(field).GetString()!);

    [Fact]
    public async Task Full_document_capture_serialises_the_whole_document_and_binds_a_ref()
    {
        var sink = new FakeDownloadSink();
        var outcome = await Runner.RunWithSinkAsync(CapturePayload(), _captureInputs, sink);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        var page = outcome.Result!.Value.GetProperty("pages")[0];

        // The var binding is the download-symmetric manifest entry — a REF, never the HTML.
        page.GetProperty("storedAs").GetString().ShouldBe($"{page.GetProperty("captureRef").GetString()}.html");
        page.GetProperty("sha256").GetString()!.Length.ShouldBe(64);
        page.GetProperty("sizeBytes").GetInt32().ShouldBeGreaterThan(0);
        page.GetProperty("stored").GetBoolean().ShouldBeTrue();

        // The stored bytes are the FULL serialised document: the doctype leads it, and the <html> element itself plus
        // the <head>/<footer> outside #content are present — exactly what innerHtml('html') would drop.
        var html = Encoding.UTF8.GetString(sink.BytesOf(Ref(outcome, "captureRef")));
        html.TrimStart().ShouldStartWith("<!"); // the doctype
        html.ShouldContain("<html");
        html.ShouldContain("<head");
        html.ShouldContain("<footer");
        html.ShouldContain("Parcel 42");
    }

    [Fact]
    public async Task Element_subtree_capture_serialises_the_element_outerHTML_only()
    {
        var sink = new FakeDownloadSink();
        var outcome = await Runner.RunWithSinkAsync(CapturePayload(), _captureInputs, sink);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        var html = Encoding.UTF8.GetString(sink.BytesOf(Ref(outcome, "contentRef")));

        // The #content subtree is the element's outerHTML — the <div id="content"> itself + descendants — NOT the
        // <html>/<head> wrapper or the sibling <footer> outside it.
        html.ShouldContain("<div id=\"content\"");
        html.ShouldContain("Parcel 42");
        html.ShouldNotContain("<html");
        html.ShouldNotContain("<footer");
    }

    [Fact]
    public async Task The_captured_document_is_byte_faithful_and_bypasses_the_credential_scrubber() // issue #70
    {
        var sink = new FakeDownloadSink();
        var outcome = await Runner.RunWithSinkAsync(CapturePayload(), _captureInputs, sink);

        var html = Encoding.UTF8.GetString(sink.BytesOf(Ref(outcome, "captureRef")));

        // The detail href carries a credential-SHAPED token= param. A result-borne innerHtml would be rewritten to
        // token=[redacted] by CredentialScrubber's param regex; the capture channel never runs it, so it survives verbatim.
        html.ShouldContain("token=abc123SECRETtoken");
        html.ShouldNotContain(CredentialScrubber.Redaction);
    }

    [Fact]
    public async Task Re_capturing_the_same_document_short_circuits_without_re_upload()
    {
        var sink = new FakeDownloadSink();
        var outcome = await Runner.RunWithSinkAsync(CapturePayload(), _captureInputs, sink);

        // doc and doc2 are the same unchanged document → one content id, uploaded once; doc2's second probe found it present.
        outcome.Result!.Value.GetProperty("pages")[0].GetProperty("reStored").GetBoolean().ShouldBeTrue();

        // Three capture nodes (doc + #content subtree + doc2), but only TWO distinct blobs (doc == doc2): two stores,
        // three exists probes — the content-addressed dedup, byte-identical to download's.
        sink.ExistsCalls.ShouldBe(3);
        sink.StoreCalls.ShouldBe(2);
        sink.Stored.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_capture_of_a_selector_that_matches_nothing_stores_an_empty_document()
    {
        // A selector matching no element yields empty outerHTML (the zero-match short-circuit, symmetric with innerHtml),
        // stored as a 0-byte content-addressed blob — the author reads sizeBytes:0 rather than a run failure.
        var sink = new FakeDownloadSink();
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "goto": { "url": "https://fixture.test/parcel/42" } },
                         { "capture": { "to": "input.captureStore", "selector": "#definitely-absent", "var": "e" } } ],
              "result": "{ size: e.sizeBytes, stored: e.stored, ref: e.contentId }" }
            """;
        var outcome = await Runner.RunWithSinkAsync(Payload, _captureInputs, sink);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        var result = outcome.Result!.Value;
        result.GetProperty("size").GetInt32().ShouldBe(0);
        result.GetProperty("stored").GetBoolean().ShouldBeTrue();
        sink.BytesOf(Guid.Parse(result.GetProperty("ref").GetString()!)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("'not a target'", InterpreterErrorCodes.InvalidCaptureTarget)]  // resolves to a non-object
    [InlineData("{ name: 'x' }", InterpreterErrorCodes.InvalidCaptureTarget)]   // object without a kind
    [InlineData("{ kind: 'nope', name: 'x' }", InterpreterErrorCodes.UnknownCaptureSink)] // kind has no sink
    public async Task A_malformed_or_unknown_capture_target_is_terminal(string toExpr, string code)
    {
        var outcome = await Runner.RunAsync(CaptureToPayload(toExpr));

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe(code);
    }

    // A minimal one-step capture payload whose `to` expression is under test; it fails at target resolution before
    // serialising anything, so the default fake backend (caphome-search) suffices.
    private static string CaptureToPayload(string toExpr)
    {
        var payload = new JsonObject
        {
            ["name"] = "t",
            ["config"] = new JsonObject { ["backend"] = "input.backend" },
            ["vars"] = new JsonObject(),
            ["steps"] = new JsonArray(new JsonObject { ["capture"] = new JsonObject { ["to"] = toExpr, ["var"] = "cap" } }),
            ["result"] = "null",
        };
        return payload.ToJsonString();
    }
}
