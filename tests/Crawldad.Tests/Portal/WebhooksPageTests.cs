using System.Net;
using System.Text.Json;
using Bunit;
using Crawldad.Contracts.Webhooks;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the webhooks page (<c>/app/webhooks</c>): the endpoint list (events, last-delivery
/// badge), the per-endpoint delivery history (selected via <c>?endpoint=</c>), the antiforgery-protected register form
/// (success redirect, required-field and API-validation errors, and the write-only secret never echoed back), and the
/// server-rendered deregister confirm (<c>?delete=</c>).</summary>
public class WebhooksPageTests : BunitContext
{
    private const string _secret = "whsec_topsecret_do_not_echo";

    public WebhooksPageTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    private void Use(IPortalTenantContext context) => Services.AddSingleton(context);

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Webhooks> RenderPage(string? query = null)
    {
        if (query is not null)
        {
            Nav.NavigateTo($"/app/webhooks{query}");
        }

        return Render<Webhooks>();
    }

    // ----- states & listing -----

    [Fact]
    public void Not_linked_shows_the_link_your_workspace_empty_state()
    {
        Use(PayloadsWebhooksTenantContext.NotLinked());

        var cut = RenderPage();

        cut.Find("[data-testid=not-linked]").TextContent.ShouldContain("No workspace yet");
        cut.FindAll("[data-testid=register-form]").ShouldBeEmpty();
    }

    [Fact]
    public void Empty_list_shows_the_no_endpoints_note_and_the_register_form()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler([])));

        var cut = RenderPage();

        cut.Find("[data-testid=no-endpoints]").ShouldNotBeNull();
        cut.Find("[data-testid=register-form]").ShouldNotBeNull();
        cut.FindAll("[data-testid=deliveries-card]").ShouldBeEmpty(); // nothing selected when there are no endpoints
    }

    [Fact]
    public void Listing_renders_events_and_last_delivery_badges()
    {
        var endpoints = new[]
        {
            Endpoint("prod", ["run.failed"], new WebhookDeliverySummary(Guid.NewGuid(), "run.failed", 2, false, 502, 87, DateTimeOffset.UnixEpoch)),
            Endpoint("ops-slack", [], last: null),
        };
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler(endpoints)));

        var cut = RenderPage();

        cut.FindAll("[data-testid=endpoint-row]").Count.ShouldBe(2);
        cut.Find("[data-testid=endpoint-count]").TextContent.ShouldBe("2");
        cut.Markup.ShouldContain("all events");              // ops-slack: empty subscription
        cut.Find("[data-testid=last-delivery]").TextContent.ShouldBe("502");
        cut.Find("[data-testid=no-delivery]").ShouldNotBeNull(); // ops-slack has never delivered
    }

    // ----- deliveries -----

    [Fact]
    public void Deliveries_default_to_the_first_endpoint_and_cover_the_badge_shapes()
    {
        var runId = Guid.NewGuid();
        var deliveries = new[]
        {
            new WebhookDeliveryItem(runId, "run.failed", 1, true, 200, 142, DateTimeOffset.UnixEpoch),
            new WebhookDeliveryItem(Guid.NewGuid(), "run.failed", 2, false, null, 30000, DateTimeOffset.UnixEpoch),
            new WebhookDeliveryItem(Guid.NewGuid(), "run.succeeded", 1, true, null, 5, DateTimeOffset.UnixEpoch),
        };
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler([Endpoint("prod", [])], ("prod", deliveries))));

        var cut = RenderPage();

        cut.Find("[data-testid=deliveries-card]").TextContent.ShouldContain("prod");
        cut.FindAll("[data-testid=delivery-row]").Count.ShouldBe(3);
        cut.Find($"a[href='/app/runs/{runId}']").ShouldNotBeNull();
        cut.Markup.ShouldContain("200");          // status code shown
        cut.Markup.ShouldContain("no response");  // transport failure, not delivered
        cut.Markup.ShouldContain("sent");         // delivered with no status code
    }

    [Fact]
    public void A_selected_endpoint_query_overrides_the_default()
    {
        var deliveries = new[] { new WebhookDeliveryItem(Guid.NewGuid(), "run.failed", 1, true, 200, 10, DateTimeOffset.UnixEpoch) };
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler([Endpoint("prod", []), Endpoint("staging", [])], ("staging", deliveries))));

        var cut = RenderPage("?endpoint=staging");

        cut.Find("[data-testid=deliveries-card]").TextContent.ShouldContain("staging");
        cut.Find("[data-testid=delivery-row]").ShouldNotBeNull();
    }

    [Fact]
    public void A_selected_endpoint_with_no_deliveries_says_so()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler([Endpoint("prod", [])], ("prod", []))));

        RenderPage().Find("[data-testid=deliveries-empty]").ShouldNotBeNull();
    }

    [Fact]
    public void An_unknown_selected_endpoint_is_surfaced_not_thrown()
    {
        // The endpoint list is empty, but ?endpoint=ghost still asks for deliveries → a 404 from the API.
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler([])));

        RenderPage("?endpoint=ghost").Find("[data-testid=deliveries-unknown]").ShouldNotBeNull();
    }

    // ----- register form -----

    [Fact]
    public void Registering_puts_the_endpoint_with_its_events_and_secret_then_redirects()
    {
        var handler = Handler([]);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderPage();

        cut.Instance.Register.Name = "prod";
        cut.Instance.Register.Url = "https://hooks.test/x";
        cut.Instance.Register.Secret = _secret;
        cut.Instance.Register.Succeeded = true;
        cut.Instance.Register.Failed = true;
        cut.Instance.Register.Cancelled = true;
        cut.Find("[data-testid=register-form]").Submit();

        var put = handler.Requests.Single(request => request.Method == HttpMethod.Put);
        put.Path.ShouldBe("/webhooks/prod");
        using var body = JsonDocument.Parse(put.Body);
        body.RootElement.GetProperty("secret").GetString().ShouldBe(_secret);
        body.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["run.succeeded", "run.failed", "run.cancelled"]);
        Nav.Uri.ShouldEndWith("/app/webhooks?registered=prod");
        cut.Markup.ShouldNotContain(_secret); // never echoed
    }

    [Fact]
    public void Registering_with_no_events_selected_sends_null_events()
    {
        var handler = Handler([]);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderPage();

        cut.Instance.Register.Name = "prod";
        cut.Instance.Register.Url = "https://hooks.test/x";
        cut.Instance.Register.Secret = _secret;
        cut.Find("[data-testid=register-form]").Submit();

        var put = handler.Requests.Single(request => request.Method == HttpMethod.Put);
        using var body = JsonDocument.Parse(put.Body);
        body.RootElement.GetProperty("events").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void A_blank_form_shows_an_error_without_calling_the_api()
    {
        // Submitting the freshly-seeded (all-null) form exercises the null-coalescing on every field and the
        // required-field guard.
        var handler = Handler([]);
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderPage();

        cut.Find("[data-testid=register-form]").Submit();

        cut.Find("[data-testid=register-error]").TextContent.ShouldContain("required");
        handler.Requests.ShouldNotContain(request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public void An_api_validation_error_is_surfaced_and_the_secret_is_not_echoed()
    {
        var handler = Handler([], put: _ =>
            ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, """{ "errors": { "secret": ["secret must be at least 16 characters"] } }"""));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderPage();

        cut.Instance.Register.Name = "prod";
        cut.Instance.Register.Url = "https://hooks.test/x";
        cut.Instance.Register.Secret = "short";
        cut.Find("[data-testid=register-form]").Submit();

        cut.Find("[data-testid=register-error]").TextContent.ShouldContain("at least 16 characters");
        cut.Markup.ShouldNotContain("short");
    }

    [Fact]
    public void A_successful_registration_flashes_the_confirmation()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler([])));

        RenderPage("?registered=prod").Find("[data-testid=registered]").TextContent.ShouldContain("prod");
    }

    // ----- deregister -----

    [Fact]
    public void Delete_query_for_a_known_endpoint_shows_the_confirm_and_submitting_deletes_it()
    {
        var handler = Handler([Endpoint("prod", [])], ("prod", []));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderPage("?delete=prod");

        cut.Find("[data-testid=delete-confirm]").TextContent.ShouldContain("prod");
        cut.Instance.Removal.Name = "prod";
        cut.Find("[data-testid=delete-form]").Submit();

        handler.Requests.ShouldContain(request => request.Method == HttpMethod.Delete && string.Equals(request.Path, "/webhooks/prod", StringComparison.Ordinal));
        Nav.Uri.ShouldEndWith("/app/webhooks");
    }

    [Fact]
    public void Delete_query_for_an_unknown_endpoint_shows_no_confirm()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(Handler([Endpoint("prod", [])], ("prod", []))));

        RenderPage("?delete=ghost").FindAll("[data-testid=delete-confirm]").ShouldBeEmpty();
    }

    [Fact]
    public void Deleting_an_already_gone_endpoint_is_idempotent()
    {
        var handler = Handler([Endpoint("prod", [])], ("prod", []), delete: _ => ClientTestHarness.Empty(HttpStatusCode.NotFound));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderPage("?delete=prod");

        cut.Instance.Removal.Name = "prod";
        cut.Find("[data-testid=delete-form]").Submit();

        Nav.Uri.ShouldEndWith("/app/webhooks"); // no throw; still redirects
    }

    [Fact]
    public void Submitting_the_delete_with_no_target_just_returns_to_the_list()
    {
        var handler = Handler([Endpoint("prod", [])], ("prod", []));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));
        var cut = RenderPage("?delete=prod");

        // Removal.Name left at its seeded null — no DELETE, just a redirect back.
        cut.Find("[data-testid=delete-form]").Submit();

        handler.Requests.ShouldNotContain(request => request.Method == HttpMethod.Delete);
        Nav.Uri.ShouldEndWith("/app/webhooks");
    }

    // ----- helpers -----

    private static WebhookSummary Endpoint(string name, IReadOnlyList<string> events, WebhookDeliverySummary? last = null) =>
        new(name, $"https://hooks.test/{name}", events, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, last);

    // A stub answering GET /webhooks, GET /webhooks/{name}/deliveries, and (optionally overridden) PUT/DELETE.
    private static StubHttpMessageHandler Handler(
        IReadOnlyList<WebhookSummary> endpoints,
        (string Name, IReadOnlyList<WebhookDeliveryItem> Items)? deliveries = null,
        Func<CapturedRequest, HttpResponseMessage>? put = null,
        Func<CapturedRequest, HttpResponseMessage>? delete = null)
    {
        return new StubHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                return put?.Invoke(req) ?? ClientTestHarness.Json(
                    new WebhookSummary("prod", "https://hooks.test/x", [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
            }

            if (req.Method == HttpMethod.Delete)
            {
                return delete?.Invoke(req) ?? ClientTestHarness.Empty(HttpStatusCode.NoContent);
            }

            if (req.Path.EndsWith("/deliveries", StringComparison.Ordinal))
            {
                return deliveries is { } d && req.Path.Contains($"/webhooks/{d.Name}/", StringComparison.Ordinal)
                    ? ClientTestHarness.Json(new WebhookDeliveryResponse(d.Items))
                    : ClientTestHarness.Empty(HttpStatusCode.NotFound);
            }

            return ClientTestHarness.Json(new WebhookListResponse(endpoints));
        });
    }
}
