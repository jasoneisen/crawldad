using System.Net;
using System.Security.Claims;
using Bunit;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the account page's <b>API keys</b> section (fake tenant context + stub SDK handler): the
/// not-linked / operator-managed / error states, the key list with status badges and row actions, the show-once mint and
/// rotate (never persisted, the session-key rotation re-links the portal), and the revoke confirm + refusal surfacing.
/// All keys here are synthetic test values.</summary>
public class AccountKeysComponentTests : BunitContext
{
    private static readonly TenantProfileResponse _profile = new("tenant-alpha", "alpha@crawldad.test", "Team", 5, 100);
    private static readonly UsageResponse _usage = new(new UsageSlots(1, 5), new UsageQueueStats(0, 0, 0), 0, new UsageEvents(0, 0, 0, 0));

    public AccountKeysComponentTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    // ---- states --------------------------------------------------------------------------------------------------

    [Fact]
    public void Not_linked_shows_the_manage_keys_empty_state()
    {
        var cut = RenderPage(new FakeTenantContext(tenant: null));

        cut.Find("[data-testid=keys-unlinked]").ShouldNotBeNull();
        cut.FindAll("[data-testid=mint-form]").ShouldBeEmpty();
    }

    [Fact]
    public void An_env_tenant_sees_the_operator_managed_note()
    {
        // GET /tenant/keys → 400 self_service_unavailable (its keys are operator config).
        var cut = RenderLinked(Api(_ => ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest,
            """{"title":"self_service_unavailable","detail":"operator-managed","status":400}""")));

        cut.Find("[data-testid=keys-operator-managed]").ShouldNotBeNull();
        cut.FindAll("[data-testid=mint-form]").ShouldBeEmpty();
    }

    [Fact]
    public void A_keys_read_error_degrades_cleanly()
    {
        var cut = RenderLinked(Api(_ => ClientTestHarness.JsonRaw(HttpStatusCode.InternalServerError, "{}")));

        cut.Find("[data-testid=keys-error]").ShouldNotBeNull();
        cut.FindAll("[data-testid=mint-form]").ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_key_list_shows_the_mint_form()
    {
        var cut = RenderLinked(Api(_ => ClientTestHarness.Json(new TenantApiKeyList([]))));

        cut.Find("[data-testid=keys-empty]").ShouldNotBeNull();
        cut.Find("[data-testid=mint-form]").ShouldNotBeNull();
    }

    [Fact]
    public void The_key_list_renders_status_badges_and_row_actions()
    {
        var current = Key(Guid.NewGuid(), "ck_test_CUR", "portal", active: true, current: true, lastUsed: DateTimeOffset.UnixEpoch);
        var other = Key(Guid.NewGuid(), "ck_test_OTH", label: null, active: true, current: false);
        var revoked = Key(Guid.NewGuid(), "ck_test_REV", "old-ci", active: false, current: false);
        var cut = RenderLinked(Api(_ => ClientTestHarness.Json(new TenantApiKeyList([current, other, revoked]))));

        cut.FindAll("[data-testid=key-row]").Count.ShouldBe(3);
        cut.FindAll("[data-testid=key-status-current]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=key-status-active]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=key-status-revoked]").Count.ShouldBe(1);

        // Rotate is offered for both active keys; revoke only for the active NON-current key (never the session key).
        cut.FindAll("[data-testid=rotate-link]").Count.ShouldBe(2);
        cut.FindAll("[data-testid=revoke-link]").Count.ShouldBe(1);
    }

    // ---- mint (show once) ----------------------------------------------------------------------------------------

    [Fact]
    public void Minting_a_key_shows_it_once_and_does_not_persist_it()
    {
        const string raw = "ck_test_MINTED_secret_value";
        var linker = new FakeLinker();
        var cut = RenderLinked(Api(req => req.Method == HttpMethod.Post
            ? ClientTestHarness.Json(new TenantApiKeyCreated(Guid.NewGuid(), "ck_test_MINTED", null, raw, DateTimeOffset.UnixEpoch), HttpStatusCode.Created)
            : ClientTestHarness.Json(new TenantApiKeyList([]))), linker: linker);

        cut.Find("[data-testid=mint-form]").Submit(); // no label → an unlabelled key

        cut.Find("[data-testid=minted-key-value]").GetAttribute("value").ShouldBe(raw);
        cut.Markup.ShouldContain("only time the full key is shown");
        linker.Calls.ShouldBeEmpty(); // minting never re-links the portal
    }

    [Fact]
    public void A_label_validation_error_is_surfaced()
    {
        var cut = RenderLinked(Api(req => req.Method == HttpMethod.Post
            ? ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, """{"errors":{"label":["label must be at most 64 characters"]}}""")
            : ClientTestHarness.Json(new TenantApiKeyList([]))));

        cut.Instance.Mint.Label = new string('x', 65);
        cut.Find("[data-testid=mint-form]").Submit();

        cut.Find("[data-testid=keys-action-error]").TextContent.ShouldContain("64");
        cut.FindAll("[data-testid=minted-key]").ShouldBeEmpty();
    }

    [Fact]
    public void A_transport_failure_when_minting_is_surfaced_friendly()
    {
        var cut = RenderLinked(Api(req => req.Method == HttpMethod.Post
            ? throw new HttpRequestException("api down")
            : ClientTestHarness.Json(new TenantApiKeyList([]))));

        cut.Find("[data-testid=mint-form]").Submit();

        cut.Find("[data-testid=keys-action-error]").TextContent.ShouldContain("reach the API");
    }

    // ---- rotate --------------------------------------------------------------------------------------------------

    [Fact]
    public void Rotating_a_non_session_key_shows_the_replacement_once_without_relinking()
    {
        var target = Key(Guid.NewGuid(), "ck_test_OTH", "ci", active: true, current: false);
        var session = Key(Guid.NewGuid(), "ck_test_CUR", "portal", active: true, current: true);
        var linker = new FakeLinker();
        var cut = RenderLinked(
            Api(req => req.Method == HttpMethod.Post
                ? ClientTestHarness.Json(new TenantApiKeyCreated(Guid.NewGuid(), "ck_test_NEW", "ci", "ck_test_NEW_secret", DateTimeOffset.UnixEpoch), HttpStatusCode.Created)
                : ClientTestHarness.Json(new TenantApiKeyList([target, session]))),
            query: $"?rotate={target.KeyId}",
            linker: linker);

        cut.Find("[data-testid=rotate-confirm]").ShouldNotBeNull();
        cut.Instance.RotateForm.KeyId = target.KeyId;
        cut.Find("[data-testid=rotate-form]").Submit();

        cut.Find("[data-testid=minted-key-value]").GetAttribute("value").ShouldBe("ck_test_NEW_secret");
        cut.Markup.ShouldContain("(ci)"); // the replacement carries the label
        linker.Calls.ShouldBeEmpty();      // a non-session key rotation doesn't touch the stored portal key
    }

    [Fact]
    public void Rotating_the_session_key_relinks_the_portal_and_prompts_a_refresh()
    {
        var session = Key(Guid.NewGuid(), "ck_test_CUR", "portal", active: true, current: true);
        var linker = new FakeLinker();
        var cut = RenderLinked(
            Api(req => req.Method == HttpMethod.Post
                ? ClientTestHarness.Json(new TenantApiKeyCreated(Guid.NewGuid(), "ck_test_NEW", "portal", "ck_test_NEW_secret", DateTimeOffset.UnixEpoch), HttpStatusCode.Created)
                : ClientTestHarness.Json(new TenantApiKeyList([session]))),
            query: $"?rotate={session.KeyId}",
            linker: linker);

        cut.Instance.RotateForm.KeyId = session.KeyId;
        cut.Find("[data-testid=rotate-form]").Submit();

        cut.Find("[data-testid=minted-key-value]").GetAttribute("value").ShouldBe("ck_test_NEW_secret");
        cut.Find("[data-testid=keys-stale]").ShouldNotBeNull();     // the old client can't re-list — prompt a refresh
        linker.Calls.ShouldHaveSingleItem();                        // the portal re-links to the replacement
        linker.Calls[0].ApiKey.ShouldBe("ck_test_NEW_secret");
    }

    [Fact]
    public void Rotating_a_missing_key_is_surfaced_friendly()
    {
        var target = Key(Guid.NewGuid(), "ck_test_OTH", null, active: true, current: false);
        var cut = RenderLinked(
            Api(req => req.Method == HttpMethod.Post
                ? ClientTestHarness.Empty(HttpStatusCode.NotFound)
                : ClientTestHarness.Json(new TenantApiKeyList([target, Key(Guid.NewGuid(), "ck_test_CUR", null, true, true)]))),
            query: $"?rotate={target.KeyId}");

        cut.Instance.RotateForm.KeyId = target.KeyId;
        cut.Find("[data-testid=rotate-form]").Submit();

        cut.Find("[data-testid=keys-action-error]").TextContent.ShouldContain("no longer exists");
    }

    [Fact]
    public void An_unexpected_rotate_error_is_surfaced()
    {
        var target = Key(Guid.NewGuid(), "ck_test_OTH", null, active: true, current: false);
        var cut = RenderLinked(
            Api(req => req.Method == HttpMethod.Post
                ? ClientTestHarness.JsonRaw(HttpStatusCode.InternalServerError, """{"detail":"boom"}""")
                : ClientTestHarness.Json(new TenantApiKeyList([target, Key(Guid.NewGuid(), "ck_test_CUR", null, true, true)]))),
            query: $"?rotate={target.KeyId}");

        cut.Instance.RotateForm.KeyId = target.KeyId;
        cut.Find("[data-testid=rotate-form]").Submit();

        cut.Find("[data-testid=keys-action-error]").ShouldNotBeNull();
    }

    [Fact]
    public void A_transport_failure_when_rotating_is_surfaced_friendly()
    {
        var target = Key(Guid.NewGuid(), "ck_test_OTH", null, active: true, current: false);
        var cut = RenderLinked(
            Api(req => req.Method == HttpMethod.Post
                ? throw new HttpRequestException("api down")
                : ClientTestHarness.Json(new TenantApiKeyList([target, Key(Guid.NewGuid(), "ck_test_CUR", null, true, true)]))),
            query: $"?rotate={target.KeyId}");

        cut.Instance.RotateForm.KeyId = target.KeyId;
        cut.Find("[data-testid=rotate-form]").Submit();

        cut.Find("[data-testid=keys-action-error]").TextContent.ShouldContain("reach the API");
    }

    // ---- revoke --------------------------------------------------------------------------------------------------

    [Fact]
    public void Revoking_a_key_deletes_it_and_redirects_to_refresh()
    {
        var target = Key(Guid.NewGuid(), "ck_test_OTH", null, active: true, current: false);
        var handler = Api(req => req.Method == HttpMethod.Delete
            ? ClientTestHarness.Empty(HttpStatusCode.NoContent)
            : ClientTestHarness.Json(new TenantApiKeyList([target, Key(Guid.NewGuid(), "ck_test_CUR", null, true, true)])));
        var cut = RenderLinked(handler, query: $"?revoke={target.KeyId}");

        cut.Find("[data-testid=revoke-confirm]").ShouldNotBeNull();
        cut.Instance.RevokeForm.KeyId = target.KeyId;
        cut.Find("[data-testid=revoke-form]").Submit();

        handler.Requests.ShouldContain(r => r.Method == HttpMethod.Delete && r.Path == $"/tenant/keys/{target.KeyId}");
        Nav.Uri.ShouldEndWith("/app/account");
    }

    [Fact]
    public void Revoking_an_already_gone_key_still_redirects()
    {
        var target = Key(Guid.NewGuid(), "ck_test_OTH", null, active: true, current: false);
        var cut = RenderLinked(
            Api(req => req.Method == HttpMethod.Delete
                ? ClientTestHarness.Empty(HttpStatusCode.NotFound)
                : ClientTestHarness.Json(new TenantApiKeyList([target, Key(Guid.NewGuid(), "ck_test_CUR", null, true, true)]))),
            query: $"?revoke={target.KeyId}");

        cut.Instance.RevokeForm.KeyId = target.KeyId;
        cut.Find("[data-testid=revoke-form]").Submit();

        Nav.Uri.ShouldEndWith("/app/account"); // idempotent — no error, still redirects
    }

    [Fact]
    public void A_refused_revoke_surfaces_the_rotate_guidance()
    {
        var target = Key(Guid.NewGuid(), "ck_test_OTH", null, active: true, current: false);
        var cut = RenderLinked(
            Api(req => req.Method == HttpMethod.Delete
                ? ClientTestHarness.JsonRaw(HttpStatusCode.Conflict, """{"title":"last_active_key","detail":"cannot revoke the tenant's last active key; rotate it","status":409}""")
                : ClientTestHarness.Json(new TenantApiKeyList([target, Key(Guid.NewGuid(), "ck_test_CUR", null, true, true)]))),
            query: $"?revoke={target.KeyId}");

        cut.Instance.RevokeForm.KeyId = target.KeyId;
        cut.Find("[data-testid=revoke-form]").Submit();

        cut.Find("[data-testid=keys-action-error]").TextContent.ShouldContain("rotate");
        Nav.Uri.ShouldEndWith($"?revoke={target.KeyId}"); // stayed on the page, no redirect
    }

    [Fact]
    public void A_transport_failure_when_revoking_is_surfaced_friendly()
    {
        var target = Key(Guid.NewGuid(), "ck_test_OTH", null, active: true, current: false);
        var cut = RenderLinked(
            Api(req => req.Method == HttpMethod.Delete
                ? throw new HttpRequestException("api down")
                : ClientTestHarness.Json(new TenantApiKeyList([target, Key(Guid.NewGuid(), "ck_test_CUR", null, true, true)]))),
            query: $"?revoke={target.KeyId}");

        cut.Instance.RevokeForm.KeyId = target.KeyId;
        cut.Find("[data-testid=revoke-form]").Submit();

        cut.Find("[data-testid=keys-action-error]").TextContent.ShouldContain("reach the API");
    }

    [Fact]
    public void A_confirm_query_for_an_unknown_key_shows_no_confirm()
    {
        var cut = RenderLinked(
            Api(_ => ClientTestHarness.Json(new TenantApiKeyList([Key(Guid.NewGuid(), "ck_test_CUR", null, true, true)]))),
            query: $"?revoke={Guid.NewGuid()}");

        cut.FindAll("[data-testid=revoke-confirm]").ShouldBeEmpty();
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private IRenderedComponent<Account> RenderLinked(StubHttpMessageHandler handler, string? query = null, HttpContext? http = null, FakeLinker? linker = null) =>
        RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), query, http, linker);

    private IRenderedComponent<Account> RenderPage(IPortalTenantContext ctx, string? query = null, HttpContext? http = null, FakeLinker? linker = null)
    {
        Services.AddSingleton(ctx);
        Services.AddSingleton<IWorkspaceLinker>(linker ?? new FakeLinker());
        if (query is not null)
        {
            Nav.NavigateTo($"/app/account{query}");
        }

        return Render<Account>(ps => ps.AddCascadingValue<HttpContext>(http ?? Authed()));
    }

    // Serves valid tenant/usage reads (so LoadAsync populates the linked state) and delegates every /tenant/keys/* call.
    private static StubHttpMessageHandler Api(Func<CapturedRequest, HttpResponseMessage> keys) =>
        new(req =>
            req.Path.Contains("/tenant/keys", StringComparison.Ordinal) ? keys(req)
            : req.Path.EndsWith("/usage", StringComparison.Ordinal) ? ClientTestHarness.Json(_usage)
            : ClientTestHarness.Json(_profile)); // /tenant and /billing/config both tolerate the profile shape

    private static TenantApiKeyInfo Key(Guid id, string prefix, string? label, bool active, bool current, DateTimeOffset? lastUsed = null) =>
        new(id, prefix, label, DateTimeOffset.UnixEpoch, lastUsed, active ? null : DateTimeOffset.UnixEpoch, active, current);

    private static PortalTenant Linked(string tenantId, HttpMessageHandler handler) =>
        new(tenantId, new CrawldadClient(new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey }));

    private static DefaultHttpContext Authed(string email = "owner@example.com") =>
        new() { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, email)], authenticationType: "TestCookie")) };

    private sealed class FakeTenantContext(PortalTenant? tenant) : IPortalTenantContext
    {
        public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant);

        public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) =>
            tenant is null ? throw new NotLinkedException("not linked") : Task.FromResult(tenant);
    }

    private sealed class FakeLinker : IWorkspaceLinker
    {
        public List<(string Email, string TenantId, string ApiKey)> Calls { get; } = [];

        public Task<WorkspaceLinkResult> LinkAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default)
        {
            Calls.Add((email, tenantId, apiKey));
            return Task.FromResult(new WorkspaceLinkResult(WorkspaceLinkOutcome.Linked, "ok"));
        }
    }
}
