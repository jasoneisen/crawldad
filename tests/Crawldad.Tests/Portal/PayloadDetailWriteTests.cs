using System.Globalization;
using System.Net;
using System.Text.Json;
using Bunit;
using Crawldad.Contracts.Drift;
using Crawldad.Contracts.Payloads;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the payload-detail WRITE surface (issue #119): the server-computed revision DIFF
/// (<c>?against=M</c>), the non-destructive well-formedness "Validate" pre-flight, and the validate-then-save "Save
/// revision" flow (PRG redirect on success; the API's problem details rendered verbatim with the user's text preserved
/// on rejection). The crawldad-DSL gate stays server-side — the portal only surfaces what the API returns.</summary>
public class PayloadDetailWriteTests : BunitContext
{
    private static readonly Guid _id = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
    private const string _name = "county.parcel.search-detail";
    private const string _validScript = """{ "crawldad": "1", "name": "county.parcel.search-detail", "steps": [] }""";

    public PayloadDetailWriteTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    private void Use(IPortalTenantContext context) => Services.AddSingleton(context);

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<PayloadDetail> RenderDetail(string? query = null)
    {
        if (query is not null)
        {
            Nav.NavigateTo($"/app/payloads/{_id}{query}");
        }

        return Render<PayloadDetail>(ps => ps.Add(p => p.PayloadId, _id.ToString()));
    }

    // ----- editor presence -----

    [Fact]
    public void Active_payload_shows_the_editor_seeded_from_the_viewed_revision_with_two_intents()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler(head: 5)));

        var cut = RenderDetail();

        cut.Find("[data-testid=editor-card]").ShouldNotBeNull();
        cut.Find("[data-testid=validate-btn]").ShouldNotBeNull();
        cut.Find("[data-testid=save-btn]").ShouldNotBeNull();
        // The textarea is seeded with the pretty-printed viewed revision (r5); InputTextArea binds via the value attribute.
        cut.Find("[data-testid=editor-script]").GetAttribute("value")!.ShouldContain("\"rev\": 5");
    }

    [Fact]
    public void Archived_payload_shows_a_read_only_note_and_no_editor()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler(head: 3, status: PayloadStatus.Archived)));

        var cut = RenderDetail();

        cut.Find("[data-testid=archived-note]").TextContent.ShouldContain("archived");
        cut.FindAll("[data-testid=editor-card]").ShouldBeEmpty();
    }

    // ----- validate (non-destructive well-formedness) -----

    [Fact]
    public void Validate_on_well_formed_json_shows_the_note_and_calls_no_api()
    {
        var handler = Handler(head: 2);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderDetail();

        cut.Instance.Editor.Script = _validScript;
        cut.Instance.Editor.Intent = "validate";
        cut.Find("[data-testid=revise-form]").Submit();

        cut.Find("[data-testid=validated]").TextContent.ShouldContain("well-formed");
        handler.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public void Validate_on_malformed_json_shows_a_parse_problem_and_preserves_text()
    {
        var handler = Handler(head: 2);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderDetail();

        cut.Instance.Editor.Script = "{ not valid json";
        cut.Instance.Editor.Intent = "validate";
        cut.Find("[data-testid=revise-form]").Submit();

        cut.Find("[data-testid=problem-code]").TextContent.ShouldBe("invalid_json");
        cut.Find("[data-testid=editor-script]").GetAttribute("value")!.ShouldContain("{ not valid json"); // preserved
        handler.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    // ----- save / revise -----

    [Fact]
    public void Save_persists_a_new_revision_with_its_note_and_redirects_to_the_new_head()
    {
        var handler = Handler(head: 5); // default revise → new head r6
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderDetail();

        cut.Instance.Editor.Script = _validScript;
        cut.Instance.Editor.Note = "tighten record require()";
        cut.Instance.Editor.Intent = "save";
        cut.Find("[data-testid=revise-form]").Submit();

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        post.Path.ShouldBe($"/payloads/{_id}/revise");
        using var body = JsonDocument.Parse(post.Body);
        body.RootElement.GetProperty("payload").GetProperty("name").GetString().ShouldBe(_name);
        body.RootElement.GetProperty("note").GetString().ShouldBe("tighten record require()");
        Nav.Uri.ShouldEndWith($"/app/payloads/{_id}?rev=6&saved=6");
    }

    [Fact]
    public void Save_without_a_note_sends_a_null_note()
    {
        var handler = Handler(head: 5);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderDetail();

        cut.Instance.Editor.Script = _validScript;
        cut.Instance.Editor.Intent = "save";
        cut.Find("[data-testid=revise-form]").Submit();

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        using var body = JsonDocument.Parse(post.Body);
        body.RootElement.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
        Nav.Uri.ShouldEndWith($"/app/payloads/{_id}?rev=6&saved=6");
    }

    [Fact]
    public void Save_with_a_validation_failure_renders_the_problems_verbatim_and_preserves_text()
    {
        var handler = Handler(head: 5, revise: _ => ClientTestHarness.JsonRaw(
            HttpStatusCode.BadRequest,
            """{ "errors": [ { "path": "/steps/6/loop", "code": "missing_max_iterations", "message": "loop needs maxIterations" } ] }"""));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderDetail();

        cut.Instance.Editor.Script = _validScript;
        cut.Instance.Editor.Intent = "save";
        cut.Find("[data-testid=revise-form]").Submit();

        var problem = cut.Find("[data-testid=payload-problem]");
        problem.TextContent.ShouldContain("/steps/6/loop");
        problem.TextContent.ShouldContain("missing_max_iterations");
        problem.TextContent.ShouldContain("loop needs maxIterations");
        // The user's submitted text is preserved for another edit, and no PRG redirect fired.
        cut.Find("[data-testid=editor-script]").GetAttribute("value")!.ShouldContain("\"crawldad\": \"1\"");
        Nav.Uri.ShouldNotContain("saved");
    }

    [Fact]
    public void Save_of_a_blank_body_is_an_invalid_json_problem_and_calls_no_api()
    {
        var handler = Handler(head: 5);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderDetail();

        cut.Instance.Editor.Script = ""; // cleared the seeded template
        cut.Instance.Editor.Intent = "save";
        cut.Find("[data-testid=revise-form]").Submit();

        cut.Find("[data-testid=problem-code]").TextContent.ShouldBe("invalid_json");
        handler.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    // ----- diff -----

    [Fact]
    public void A_diff_renders_the_server_structural_changes_across_all_kinds()
    {
        var added = JsonElementOf("""{ "waitForLoadState": { "state": "load" } }""");
        var removed = JsonElementOf("\"#old\"");
        var handler = Handler(head: 5, diff: req =>
        {
            var (from, to) = DiffRevs(req.Path);
            return ClientTestHarness.Json(new PayloadDiffResponse(_id, from, to, Script(from), Script(to),
            [
                new PayloadDiffEntry("/steps/3/click/selector", PayloadDiffKind.Changed, JsonElementOf("\"#a\""), JsonElementOf("\"#b\"")),
                new PayloadDiffEntry("/steps/9", PayloadDiffKind.Added, null, added),
                new PayloadDiffEntry("/config/strictExtraction", PayloadDiffKind.Removed, removed, null),
            ]));
        });
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = RenderDetail("?rev=5&against=3");

        cut.Find("[data-testid=diff-result]").TextContent.ShouldContain("r3");
        cut.FindAll("[data-testid=diff-change]").Count.ShouldBe(3);
        cut.FindAll("[data-testid=diff-kind]").Select(n => n.TextContent).ShouldBe(["changed", "added", "removed"]);
        // Added rows have no "from" value; removed rows have no "to" value (the em dash).
        cut.FindAll("[data-testid=diff-from]")[1].TextContent.ShouldBe("—");
        cut.FindAll("[data-testid=diff-to]")[2].TextContent.ShouldBe("—");
        cut.Find("[data-testid=diff-clear]").ShouldNotBeNull();
        var diff = handler.Requests.Single(r => r.Path.Contains("/diff/", StringComparison.Ordinal));
        diff.Path.ShouldBe($"/payloads/{_id}/diff/3/5");
    }

    [Fact]
    public void A_same_revision_diff_reports_no_differences()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler(head: 3))); // default diff → empty changes

        var cut = RenderDetail("?rev=2&against=2");

        cut.Find("[data-testid=diff-none]").ShouldNotBeNull();
        cut.FindAll("[data-testid=diff-change]").ShouldBeEmpty();
    }

    [Fact]
    public void An_out_of_range_against_is_clamped_into_the_revision_span()
    {
        var handler = Handler(head: 5);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        // ?against=0 clamps up to r1; the viewed head is r5 → the server diff is /diff/1/5.
        RenderDetail("?rev=5&against=0");

        handler.Requests.Single(r => r.Path.Contains("/diff/", StringComparison.Ordinal)).Path
            .ShouldBe($"/payloads/{_id}/diff/1/5");
    }

    [Fact]
    public void The_default_view_requests_no_diff_but_still_offers_compare_links()
    {
        var handler = Handler(head: 5);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = RenderDetail();

        cut.FindAll("[data-testid=diff-result]").ShouldBeEmpty();
        cut.FindAll("[data-testid=compare-link]").Count.ShouldBe(4); // r1..r4 (not the viewed r5)
        handler.Requests.ShouldNotContain(r => r.Path.Contains("/diff/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_revision_payload_offers_no_compare_card()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler(head: 1)));

        // ?against=1 on a one-revision payload: no diff card, no diff request.
        var cut = RenderDetail("?against=1");

        cut.FindAll("[data-testid=diff-card]").ShouldBeEmpty();
        cut.FindAll("[data-testid=diff-result]").ShouldBeEmpty();
    }

    [Fact]
    public void A_diff_read_that_404s_degrades_to_no_diff_but_keeps_the_page()
    {
        var handler = Handler(head: 5, diff: _ => ClientTestHarness.Empty(HttpStatusCode.NotFound));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = RenderDetail("?rev=5&against=3");

        cut.FindAll("[data-testid=diff-result]").ShouldBeEmpty();
        cut.Find("[data-testid=script]").ShouldNotBeNull(); // the rest of the page still renders
    }

    // ----- saved flash -----

    [Fact]
    public void The_saved_flash_confirms_the_new_head()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler(head: 6)));

        RenderDetail("?rev=6&saved=6").Find("[data-testid=saved]").TextContent.ShouldContain("r6");
    }

    // ----- helpers -----

    private static StubHttpMessageHandler Handler(
        int head = 5,
        PayloadStatus status = PayloadStatus.Active,
        Func<CapturedRequest, HttpResponseMessage>? revise = null,
        Func<CapturedRequest, HttpResponseMessage>? diff = null)
    {
        return new StubHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.Path.EndsWith("/revise", StringComparison.Ordinal))
            {
                return revise?.Invoke(req)
                    ?? ClientTestHarness.Json(new PayloadResponse(_id, _name, head + 1, "hash-new", PayloadStatus.Active));
            }

            if (req.Path.Contains("/diff/", StringComparison.Ordinal))
            {
                if (diff is not null)
                {
                    return diff(req);
                }

                var (from, to) = DiffRevs(req.Path);
                return ClientTestHarness.Json(new PayloadDiffResponse(_id, from, to, Script(from), Script(to), []));
            }

            if (req.Path.EndsWith("/drift-status", StringComparison.Ordinal))
            {
                return ClientTestHarness.Empty(HttpStatusCode.NotFound); // no canary → no drift card (keeps the DOM focused)
            }

            if (req.Path.Contains("/revisions/", StringComparison.Ordinal))
            {
                var rev = int.Parse(req.Path[(req.Path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
                return ClientTestHarness.Json(new PayloadRevisionResponse(_id, rev, "hash", Script(rev)));
            }

            return ClientTestHarness.Json(new PayloadResponse(_id, _name, head, "hash", status));
        });
    }

    private static (int From, int To) DiffRevs(string path)
    {
        var parts = path.Split('/');
        return (int.Parse(parts[^2], CultureInfo.InvariantCulture), int.Parse(parts[^1], CultureInfo.InvariantCulture));
    }

    private static JsonElement Script(int revision)
    {
        using var document = JsonDocument.Parse($$"""{ "rev": {{revision}} }""");
        return document.RootElement.Clone();
    }

    private static JsonElement JsonElementOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
