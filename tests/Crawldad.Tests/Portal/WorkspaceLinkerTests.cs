using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for <see cref="WorkspaceLinker"/> in isolation (stub API handler + recording store): the key is
/// always validated against the live API BEFORE anything is written, so a valid+matching key is the only path that
/// upserts (with the authoritative tenant id), and a rejected key, a wrong-tenant key, an API error, and a transport
/// failure each leave the store untouched. No outcome message ever contains the submitted key.</summary>
public class WorkspaceLinkerTests
{
    private const string _email = "owner@example.com";
    private const string _apiKey = "sk_live_probe_0123456789";

    private static (WorkspaceLinker Linker, RecordingLinkStore Store) LinkerFor(HttpMessageHandler handler)
    {
        var factory = new StubHttpClientFactory(handler, new Uri("https://api.crawldad.test/"));
        var store = new RecordingLinkStore();
        return (new WorkspaceLinker(factory, store), store);
    }

    // A path-branching handler: GET /tenant returns the profile; POST /tenant/memberships records an owner membership.
    private static StubHttpMessageHandler LinkedHandler(string tenantId = "tenant-alpha") =>
        new(req => string.Equals(req.Path, "/tenant/memberships", StringComparison.Ordinal)
            ? ClientTestHarness.Json(new TenantMembershipInfo(Guid.NewGuid(), _email, MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true))
            : ClientTestHarness.Json(new TenantProfileResponse(tenantId, "alpha@crawldad.test", "pro", 5, 20)));

    [Fact]
    public async Task Valid_key_whose_tenant_matches_is_linked_and_records_the_owner_membership()
    {
        var handler = LinkedHandler();
        var (linker, store) = LinkerFor(handler);

        // Enter the id in a different casing to prove the stored id is the profile's, not the raw entry.
        var result = await linker.LinkAsync(_email, "  Tenant-Alpha  ", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Linked);

        // The key was probed (GET /tenant), then the owner membership was recorded (POST /tenant/memberships) with the key.
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Path.ShouldBe("/tenant");
        handler.Requests[0].Authorization.ShouldBe($"Bearer {_apiKey}");
        handler.Requests[1].Method.ShouldBe(HttpMethod.Post);
        handler.Requests[1].Path.ShouldBe("/tenant/memberships");
        handler.Requests[1].Authorization.ShouldBe($"Bearer {_apiKey}");
        handler.Requests[1].Body.ShouldContain(_email);

        var upsert = store.Upserts.ShouldHaveSingleItem();
        upsert.Email.ShouldBe(_email);
        upsert.TenantId.ShouldBe("tenant-alpha"); // authoritative id from the profile, not the entered casing/padding
        upsert.ApiKey.ShouldBe(_apiKey);
    }

    [Fact]
    public async Task An_env_tenant_membership_refusal_is_swallowed_and_the_link_still_succeeds()
    {
        // The membership surface is registry-only; an env tenant returns 400 self_service_unavailable — which must NOT
        // block linking (the stored key still authenticates every call).
        var handler = new StubHttpMessageHandler(req => string.Equals(req.Path, "/tenant/memberships", StringComparison.Ordinal)
            ? ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, "{\"title\":\"self_service_unavailable\"}")
            : ClientTestHarness.Json(new TenantProfileResponse("tenant-alpha", "a@crawldad.test", null, 3, 10)));
        var (linker, store) = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Linked);
        store.Upserts.ShouldHaveSingleItem(); // the link was still persisted
    }

    [Fact]
    public async Task A_membership_transport_failure_is_swallowed_and_the_link_still_succeeds()
    {
        // GET /tenant succeeds; the membership POST throws a transport fault — swallowed, the link persists.
        var handler = new StubHttpMessageHandler(req => string.Equals(req.Path, "/tenant/memberships", StringComparison.Ordinal)
            ? throw new HttpRequestException("connection reset")
            : ClientTestHarness.Json(new TenantProfileResponse("tenant-alpha", "a@crawldad.test", null, 3, 10)));
        var (linker, store) = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Linked);
        store.Upserts.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_rejected_key_is_invalid_and_never_upserts_and_never_echoes_the_key()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.Unauthorized));
        var (linker, store) = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", "totally-wrong-key");

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.InvalidKey);
        result.Message.ShouldNotContain("totally-wrong-key");
        store.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_valid_key_for_a_different_tenant_is_a_mismatch_and_never_upserts()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new TenantProfileResponse("tenant-beta", "beta@crawldad.test", null, 3, 10)));
        var (linker, store) = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.TenantMismatch);
        result.Message.ShouldContain("tenant-beta"); // names the tenant the key actually authenticates
        result.Message.ShouldNotContain(_apiKey);
        store.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_api_error_is_unverifiable_and_never_upserts()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.InternalServerError));
        var (linker, store) = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Unverifiable);
        store.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transport_failure_is_unverifiable_and_never_upserts()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
        var (linker, store) = LinkerFor(handler);

        var result = await linker.LinkAsync(_email, "tenant-alpha", _apiKey);

        result.Outcome.ShouldBe(WorkspaceLinkOutcome.Unverifiable);
        store.Upserts.ShouldBeEmpty();
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

    private sealed class RecordingLinkStore : IPortalTenantLinkStore
    {
        public List<(string Email, string TenantId, string ApiKey)> Upserts { get; } = [];

        public Task<PortalTenantLink?> GetAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<PortalTenantLink?>(null);

        public Task<PortalTenantLink> UpsertAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default)
        {
            Upserts.Add((email, tenantId, apiKey));
            return Task.FromResult(new PortalTenantLink { Email = email, TenantId = tenantId });
        }
    }
}
