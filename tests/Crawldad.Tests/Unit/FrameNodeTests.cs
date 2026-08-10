using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>Frames (<c>frame</c>, <c>in:</c>) and <c>addStyleTag</c>: the CapDetail attachments iframe traversal —
/// a <c>FrameLocator</c> bound to a var, <c>in:</c> rooting locate/click/fill/clear/waitFor and structured Sels
/// inside it, DOM builtins over frame-bound handles, in-frame downloads, and in-frame pagination.</summary>
public class FrameNodeTests
{
    // The attachments fixture + a fake attachment store for the per-row download.
    private const string _attInputs =
        """{ "backend": { "adapter": "fake", "options": { "fixture": "capdetail-attachments" } }, "attachmentStore": { "kind": "fake", "name": "attachmentStore" } }""";

    // Navigate to the record and bind the attachments iframe to `attFrame` — the shared prefix for the frame tests.
    private const string _gotoAndFrame =
        """{ "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID=24ENF-ATT01&agencyCode=LJCMG" } },""" +
        """ { "frame": { "var": "attFrame", "selector": "#ctl00_PlaceHolderMain_attachmentEdit_iframeAttachmentList" } }""";

    private static string Payload(string steps, string result = "null", string vars = "{}") =>
        $$"""{ "name": "att", "config": { "backend": "input.backend" }, "vars": {{vars}}, "steps": {{steps}}, "result": "{{result}}" }""";

    private static Task<RunOutcome> Run(string steps, string result = "null", string vars = "{}", string inputs = _attInputs) =>
        Runner.RunAsync(Payload(steps, result, vars), inputs);

    private static System.Text.Json.JsonElement Ok(RunOutcome outcome)
    {
        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        return outcome.Result!.Value;
    }

    private static RunFailureDetail Fail(RunOutcome outcome)
    {
        outcome.Status.ShouldBe(RunStatus.Failed);
        return outcome.Failure!;
    }

    // ----- the full attachments-iframe flow -----

    [Fact]
    public async Task Attachments_iframe_traversal_downloads_paginates_across_the_postback_and_stops()
    {
        // A page-level tab click, then the do…while over the in-frame grid: locate rows IN the frame, download the file
        // row (in-frame click captured as a page download), then click Next (in-frame) so the frame re-renders to page 2,
        // where the "No records found." row is skipped and the missing next link stops the loop.
        var steps = $$"""
            [ {{_gotoAndFrame}},
              { "click": { "selector": { "title": "Attachments" } } },
              { "set": { "var": "attPagesVisited", "value": "0" } },
              { "loop": { "maxIterations": 100, "while": "hasMoreAttachmentPages", "do": [
                  { "locate": { "var": "attRows", "in": "attFrame", "selector": "#attachmentList_gdvAttachmentList > tbody > tr:not(.ACA_Table_Pages)" } },
                  { "if": { "cond": "count(attRows) > 1", "then": [
                    { "loop": { "maxIterations": 100, "for": { "var": "i", "from": "1", "to": "count(attRows)", "exclusiveTo": true }, "do": [
                      { "locate": { "var": "attRow", "in": "attFrame", "selector": "#attachmentList_gdvAttachmentList > tbody > tr:nth-child(${i+1})" } },
                      { "continue": { "when": "equalsIgnoreCase(trim(coalesce(text(attRow),'')), 'No records found.')" } },
                      { "locate": { "var": "fileLink", "base": "attRow", "selector": "td:first-child a" } },
                      { "continue": { "when": "count(fileLink) == 0" } },
                      { "set": { "var": "filename", "value": "trim(coalesce(text(attRow,'td:first-child'),''))" } },
                      { "set": { "var": "attType",  "value": "trim(coalesce(text(attRow,'td:nth-child(5)'),''))" } },
                      { "download": { "trigger": [ { "click": { "selector": "fileLink" } } ], "to": "input.attachmentStore", "timeoutMs": 180000, "var": "dl" } },
                      { "if": { "cond": "dl.stored", "then": [
                        { "push": { "into": "attachments", "value":
                          "{ filename: filename, type: attType, internalFilename: string(dl.contentId) + (contains(filename,'.') ? '.' + substringAfterLast(filename,'.') : '') }" } } ] } }
                    ] } }
                  ] } },
                  { "set": { "var": "attPagesVisited", "value": "attPagesVisited + 1" } },
                  { "locate": { "var": "nextAtt", "in": "attFrame", "selector": "#attachmentList_gdvAttachmentList > tbody > tr.ACA_Table_Pages table.aca_pagination > tbody > tr > td:last-child > a" } },
                  { "set": { "var": "hasNextAttLink",         "value": "count(nextAtt) > 0" } },
                  { "set": { "var": "hasMoreAttachmentPages", "value": "hasNextAttLink && attPagesVisited < 50" } },
                  { "if": { "cond": "hasMoreAttachmentPages", "then": [
                      { "set": { "var": "nextPageNumber", "value": "string(attPagesVisited + 1)" } },
                      { "click": { "selector": "nextAtt" } },
                      { "waitFor": { "in": "attFrame",
                          "selector": { "css": "table.aca_pagination span.SelectedPageButton", "filter": { "hasTextRegex": "^${nextPageNumber}$" } },
                          "timeoutMs": 360000 } }
                    ] } }
              ] } } ]
            """;

        var result = Ok(await Run(steps,
            result: "{ attachments: attachments, pagesVisited: attPagesVisited }",
            vars: """{ "attachments": [], "hasMoreAttachmentPages": false }"""));

        result.GetProperty("pagesVisited").GetInt64().ShouldBe(2); // page 1 (download) + page 2 (empty), then stop
        var attachments = result.GetProperty("attachments");
        attachments.GetArrayLength().ShouldBe(1);
        var only = attachments[0];
        only.GetProperty("filename").GetString().ShouldBe("Site Photo.jpg");
        only.GetProperty("type").GetString().ShouldBe("Photo");
        // internalFilename = string(contentId) + "." + substringAfterLast(scraped filename, ".") — the pinned sample.bin
        // contentId (= AttachmentHashing) with the SCRAPED ".jpg" extension.
        only.GetProperty("internalFilename").GetString().ShouldBe("18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48.jpg");
    }

    // ----- in: on fill / clear / click, and a structured Sel's own in: on a DOM builtin -----

    [Fact]
    public async Task Fill_clear_and_click_target_the_frame_document_via_in()
    {
        // fill/clear with in: mutate the FRAME's document; a structured Sel { css, in } on attr reads it back; an
        // in-frame click that matches no transition is a benign no-op, resolved inside the frame.
        var steps = $$"""
            [ {{_gotoAndFrame}},
              { "fill":  { "in": "attFrame", "selector": "#frameInput", "value": "'hello'" } },
              { "set":   { "var": "filled",  "value": "coalesce(attr({ css: '#frameInput', in: 'attFrame' }, 'value'), '<null>')" } },
              { "clear": { "in": "attFrame", "selector": "#frameInput" } },
              { "set":   { "var": "cleared", "value": "coalesce(attr({ css: '#frameInput', in: 'attFrame' }, 'value'), '<null>')" } },
              { "click": { "in": "attFrame", "selector": "#frameBtn" } } ]
            """;

        Ok(await Run(steps, result: "'' + filled + '|' + cleared")).GetString().ShouldBe("hello|");
    }

    [Fact]
    public async Task Dom_builtins_read_frame_bound_handles_and_frame_rooted_sels()
    {
        // count over a frame-bound locator var flows through IDomAccess unchanged; exists/text over a structured
        // { css, in } Sel root the DOM read inside the frame (count treats a map as a collection, so frame-content
        // counts use bound handles).
        var steps = $$"""
            [ {{_gotoAndFrame}},
              { "locate": { "var": "rows", "in": "attFrame", "selector": "#attachmentList_gdvAttachmentList > tbody > tr:not(.ACA_Table_Pages)" } } ]
            """;

        var result = Ok(await Run(steps,
            result: "{ rows: count(rows), hasDataRow: exists({ css: '#attachmentList_gdvAttachmentList > tbody > tr:nth-child(2)', in: 'attFrame' }), filename: trim(coalesce(text({ css: '#attachmentList_gdvAttachmentList > tbody > tr:nth-child(2)', in: 'attFrame' }, 'td:first-child'), '')) }"));

        result.GetProperty("rows").GetInt64().ShouldBe(2);              // header + the single data row (frame-bound handle)
        result.GetProperty("hasDataRow").GetBoolean().ShouldBeTrue();   // structured { css, in } Sel, rooted in the frame
        result.GetProperty("filename").GetString().ShouldBe("Site Photo.jpg"); // frame-rooted relative text()
    }

    // ----- error taxonomy: in: on an undefined or non-frame var -----

    [Fact]
    public async Task In_referencing_an_undefined_or_non_frame_var_is_terminal()
    {
        Fail(await Run($$"""[ {{_gotoAndFrame}}, { "locate": { "var": "y", "in": "ghost", "selector": "#a" } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.MalformedNode); // undefined var

        Fail(await Run($$"""[ {{_gotoAndFrame}}, { "set": { "var": "notAFrame", "value": "'x'" } }, { "click": { "in": "notAFrame", "selector": "#a" } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.MalformedNode); // bound, but not a frame handle
    }

    // ----- a frame selector absent in the current state resolves to an empty document -----

    [Fact]
    public async Task A_frame_selector_absent_in_the_state_resolves_to_an_empty_document()
    {
        var steps = """
            [ { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID=24ENF-ATT01&agencyCode=LJCMG" } },
              { "frame": { "var": "ghostFrame", "selector": "#no-such-iframe" } } ]
            """;

        Ok(await Run(steps, result: "exists({ css: 'tr', in: 'ghostFrame' })")).GetBoolean().ShouldBeFalse();
    }

    // ----- waitFor state present (the default-to-visible case is exercised by the flow above) -----

    [Fact]
    public async Task WaitFor_honors_an_explicit_state()
    {
        // #divGlobalLoading is display:none on the CapDetail shell, so "hidden" succeeds — the explicit-state branch.
        var steps = """
            [ { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID=24ENF-ATT01&agencyCode=LJCMG" } },
              { "waitFor": { "selector": "#divGlobalLoading", "state": "hidden" } } ]
            """;

        Ok(await Run(steps, result: "'waited'")).GetString().ShouldBe("waited");
    }

    // ----- addStyleTag records the injected CSS observably; content is a Tmpl -----

    [Fact]
    public async Task AddStyleTag_injects_css_recorded_by_the_page()
    {
        var steps =
            """
            [ { "addStyleTag": { "content": ".record-detail .record-main-section .record-tab-content { display: block !important; }" } },
              { "addStyleTag": { "content": "body { color: ${'red'}; }" } } ]
            """;

        var (outcome, backend) = await Runner.RunWithFakeAsync(Payload(steps, result: "'ok'"), Runner.FakeInputs);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        backend.LastSession!.LastPage!.InjectedStyles.ShouldBe(
        [
            ".record-detail .record-main-section .record-tab-content { display: block !important; }",
            "body { color: red; }", // the ${…} template rendered
        ]);
    }
}
