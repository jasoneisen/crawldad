using System.Net;
using System.Text.Json;
using Bunit;
using Crawldad.Contracts.Payloads;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the new-payload creation page (<c>/app/payloads/new</c>) and the registry's "New payload"
/// entry point: the not-linked empty state, the template-seeded editor, the non-destructive "Validate" pre-flight, and
/// the validate-then-create flow (draft via <c>POST /payloads</c>; PRG redirect to the new payload; API problems
/// rendered verbatim with the user's text preserved on rejection).</summary>
public class PayloadNewPageTests : BunitContext
{
    private static readonly Guid _newId = Guid.Parse("9d21f6bb-1111-2222-3333-444455556666");
    private const string _validScript = """{ "crawldad": "1", "name": "sos.business.lookup", "steps": [] }""";

    public PayloadNewPageTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    private void Use(IPortalTenantContext context) => Services.AddSingleton(context);

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    // ----- states -----

    [Fact]
    public void Not_linked_shows_the_empty_state_and_no_form()
    {
        Use(PayloadsWebhooksTenantContext.NotLinked());

        var cut = Render<PayloadNew>();

        cut.Find("[data-testid=not-linked]").TextContent.ShouldContain("No workspace yet");
        cut.FindAll("[data-testid=create-form]").ShouldBeEmpty();
    }

    [Fact]
    public void Linked_shows_the_template_seeded_editor()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(DraftHandler()));

        var cut = Render<PayloadNew>();

        cut.Find("[data-testid=create-form]").ShouldNotBeNull();
        cut.Find("[data-testid=editor-script]").GetAttribute("value")!.ShouldContain("\"crawldad\": \"1\"");
    }

    // ----- validate -----

    [Fact]
    public void Validate_on_well_formed_json_shows_the_note_and_calls_no_api()
    {
        var handler = DraftHandler();
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = Render<PayloadNew>();

        cut.Instance.Editor.Script = _validScript;
        cut.Instance.Editor.Intent = "validate";
        cut.Find("[data-testid=create-form]").Submit();

        cut.Find("[data-testid=validated]").TextContent.ShouldContain("well-formed");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_on_malformed_json_shows_a_parse_problem_and_preserves_text()
    {
        var handler = DraftHandler();
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = Render<PayloadNew>();

        cut.Instance.Editor.Script = "}{ not json";
        cut.Instance.Editor.Intent = "validate";
        cut.Find("[data-testid=create-form]").Submit();

        cut.Find("[data-testid=problem-code]").TextContent.ShouldBe("invalid_json");
        cut.Find("[data-testid=editor-script]").GetAttribute("value")!.ShouldContain("}{ not json");
        handler.Requests.ShouldBeEmpty();
    }

    // ----- create -----

    [Fact]
    public void Create_drafts_the_payload_and_redirects_to_its_detail_page()
    {
        var handler = DraftHandler();
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = Render<PayloadNew>();

        cut.Instance.Editor.Script = _validScript;
        cut.Instance.Editor.Intent = "save";
        cut.Find("[data-testid=create-form]").Submit();

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        post.Path.ShouldBe("/payloads");
        using var body = JsonDocument.Parse(post.Body);
        body.RootElement.GetProperty("payload").GetProperty("name").GetString().ShouldBe("sos.business.lookup");
        Nav.Uri.ShouldEndWith($"/app/payloads/{_newId}?rev=1&saved=1");
    }

    [Fact]
    public void Create_with_a_validation_failure_renders_problems_verbatim_and_preserves_text()
    {
        var handler = DraftHandler(_ => ClientTestHarness.JsonRaw(
            HttpStatusCode.BadRequest,
            """{ "errors": [ { "path": "", "code": "missing_name", "message": "the payload needs a name" } ] }"""));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = Render<PayloadNew>();

        cut.Instance.Editor.Script = _validScript;
        cut.Instance.Editor.Intent = "save";
        cut.Find("[data-testid=create-form]").Submit();

        var problem = cut.Find("[data-testid=payload-problem]");
        problem.TextContent.ShouldContain("(root)"); // empty JSON-Pointer path renders as (root)
        problem.TextContent.ShouldContain("missing_name");
        problem.TextContent.ShouldContain("the payload needs a name");
        cut.Find("[data-testid=editor-script]").GetAttribute("value")!.ShouldContain("sos.business.lookup"); // preserved
        Nav.Uri.ShouldNotContain("saved"); // no PRG redirect fired
    }

    // ----- registry entry point -----

    [Fact]
    public void The_registry_offers_a_new_payload_button_when_linked()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadListResponse([])));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = Render<Payloads>();

        cut.Find("[data-testid=new-payload]").GetAttribute("href").ShouldBe("/app/payloads/new");
    }

    [Fact]
    public void The_registry_hides_the_new_payload_button_when_not_linked()
    {
        Use(PayloadsWebhooksTenantContext.NotLinked());

        Render<Payloads>().FindAll("[data-testid=new-payload]").ShouldBeEmpty();
    }

    private static StubHttpMessageHandler DraftHandler(Func<CapturedRequest, HttpResponseMessage>? draft = null) =>
        new(req => req.Method == HttpMethod.Post && string.Equals(req.Path, "/payloads", StringComparison.Ordinal)
            ? draft?.Invoke(req) ?? ClientTestHarness.Json(new PayloadResponse(_newId, "sos.business.lookup", 1, "hash", PayloadStatus.Active))
            : ClientTestHarness.Empty(HttpStatusCode.NotFound));
}
