using System.Net;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for <see cref="WorkspaceLinker"/> in isolation (stub API handler): the submitted key is always
/// validated against the live API BEFORE anything is recorded, and the key is ALWAYS discarded — there is no stored key
/// anywhere (issue #119). A valid+matching key records the account's Owner membership (Claimed); a rejected key, a
/// wrong-tenant key, an operator-configured (env) tenant, an API error, and a transport failure each record nothing. No
/// outcome message ever contains the submitted key.</summary>
public class WorkspaceLinkerTests
{
    private const string _email = "owner@example.com";
    private const string _apiKey = "sk_live_probe_0123456789";

    private static WorkspaceLinker LinkerFor(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler, new Uri("https://api.crawldad.test/")));

    // A path-branching handler: GET /tenant returns the profile; POST /tenant/memberships records an owner membership.
    private static StubHttpMessageHandler LinkedHandler(string tenantId = "tenant-alpha") =>
        new(req => string.Equals(req.Path, "/tenant/memberships", StringComparison.Ordinal)
            ? ClientTestHarness.Json(new TenantMembershipInfo(Guid.NewGuid(), _email, MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true))
            : ClientTestHarness.Json(new TenantProfileResponse(tenantId, "alpha@crawldad.test", "pro", 5, 20)));

    [Fact]
    public async Task Valid_key_whose_tenant_matches_records_the_owner_membership_and_discards_the_key()
    {
        var handler = LinkedHandler();
        var linker = LinkerFor(handler);

        // Enter the id in a different casing/padding to prove the match is case-insensitive and trimmed.
        var result = await linker.LinkAsync(_email, "  Tenant-Alpha  ", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Claimed);

        // The key was probed (GET /tenant), then the owner membership was recorded (POST /tenant/memberships) with the key —
        // and the key is never stored (there is no store).
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Path.ShouldBe("/tenant");
        handler.Requests[0].Authorization.ShouldBe($"Bearer {_apiKey}");
        handler.Requests[1].Method.ShouldBe(HttpMethod.Post);
        handler.Requests[1].Path.ShouldBe("/tenant/memberships");
        handler.Requests[1].Authorization.ShouldBe($"Bearer {_apiKey}");
        handler.Requests[1].Body.ShouldContain(_email);
        result.Message.ShouldNotContain(_apiKey);
    }

    [Fact]
    public async Task An_operator_configured_env_tenant_cannot_be_claimed_and_keeps_no_key()
    {
        // An env tenant has no membership surface (400 self_service_unavailable). It can't be claimed as a workspace — and,
        // crucially, NO key is kept (the old stored-key fallback is gone). The message says so clearly.
        var handler = new StubHttpMessageHandler(req => string.Equals(req.Path, "/tenant/memberships", StringComparison.Ordinal)
            ? ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, "{\"title\":\"self_service_unavailable\"}")
            : ClientTestHarness.Json(new TenantProfileResponse("tenant-alpha", "a@crawldad.test", null, 3, 10)));
        var linker = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.OperatorManaged);
        result.Message.ShouldContain("operator-configured");
        result.Message.ShouldNotContain(_apiKey);
    }

    [Fact]
    public async Task A_membership_transport_failure_is_unverifiable()
    {
        // GET /tenant succeeds; the membership POST throws a transport fault — nothing is recorded, no key is kept.
        var handler = new StubHttpMessageHandler(req => string.Equals(req.Path, "/tenant/memberships", StringComparison.Ordinal)
            ? throw new HttpRequestException("connection reset")
            : ClientTestHarness.Json(new TenantProfileResponse("tenant-alpha", "a@crawldad.test", null, 3, 10)));
        var linker = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Unverifiable);
    }

    [Fact]
    public async Task A_rejected_key_is_invalid_and_never_echoes_the_key()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.Unauthorized));
        var linker = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", "totally-wrong-key");

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.InvalidKey);
        result.Message.ShouldNotContain("totally-wrong-key");
    }

    [Fact]
    public async Task A_valid_key_for_a_different_tenant_is_a_mismatch()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new TenantProfileResponse("tenant-beta", "beta@crawldad.test", null, 3, 10)));
        var linker = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.TenantMismatch);
        result.Message.ShouldContain("tenant-beta"); // names the tenant the key actually authenticates
        result.Message.ShouldNotContain(_apiKey);
    }

    [Fact]
    public async Task An_api_error_on_the_probe_is_unverifiable()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.InternalServerError));
        var linker = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Unverifiable);
    }

    [Fact]
    public async Task A_transport_failure_on_the_probe_is_unverifiable()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
        var linker = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Unverifiable);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
    }

    private sealed class ThrowingHttpMessageHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw ex;
    }
}
