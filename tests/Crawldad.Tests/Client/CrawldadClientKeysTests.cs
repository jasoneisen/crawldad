using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Tests.Client;

/// <summary>Unit tests for the self-service key surface over a stub handler: list / mint / rotate / revoke send the bearer
/// key to the right method+path, round-trip the <c>Crawldad.Contracts</c> shapes (the one-time raw key, the optional
/// label, the current flag), and map the server's refusals (<c>409</c> last/current key, <c>400 self_service_unavailable</c>,
/// <c>404</c>) to the typed exceptions the portal and SDK callers branch on. All keys here are synthetic.</summary>
public class CrawldadClientKeysTests
{
    [Fact]
    public async Task ListTenantKeys_sends_a_bearer_get_and_maps_the_rows()
    {
        var created = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new TenantApiKeyList(
        [
            new TenantApiKeyInfo(Guid.NewGuid(), "ck_test_AAAAAA", "ci", created, created, null, true, true),
            new TenantApiKeyInfo(Guid.NewGuid(), "ck_test_BBBBBB", null, created, null, created, false, false),
        ])));
        var client = ClientTestHarness.ClientFor(handler);

        var list = await client.ListTenantKeysAsync();

        list.Keys.Count.ShouldBe(2);
        list.Keys[0].Label.ShouldBe("ci");
        list.Keys[0].Current.ShouldBeTrue();
        list.Keys[0].Active.ShouldBeTrue();
        list.Keys[1].Label.ShouldBeNull();   // omitted-when-null round-trips as null
        list.Keys[1].Active.ShouldBeFalse();
        list.Keys[1].Current.ShouldBeFalse();

        handler.Last.Method.ShouldBe(HttpMethod.Get);
        handler.Last.Path.ShouldBe("/tenant/keys");
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }

    [Fact]
    public async Task ListTenantKeys_maps_self_service_unavailable_for_an_env_tenant()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest,
            """{"title":"self_service_unavailable","detail":"...operator-managed...","status":400}"""));
        var client = ClientTestHarness.ClientFor(handler);

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.ListTenantKeysAsync());
        ex.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task MintTenantKey_posts_the_label_and_returns_the_one_time_raw_key()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(
            new TenantApiKeyCreated(Guid.NewGuid(), "ck_test_AAAAAA", "ci", "ck_test_AAAAAA_secret", DateTimeOffset.UtcNow),
            HttpStatusCode.Created));
        var client = ClientTestHarness.ClientFor(handler);

        var minted = await client.MintTenantKeyAsync("ci");

        minted.ApiKey.ShouldBe("ck_test_AAAAAA_secret");
        minted.Label.ShouldBe("ci");

        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/tenant/keys");
        handler.Last.Body.ShouldContain("\"label\":\"ci\"");
    }

    [Fact]
    public async Task MintTenantKey_maps_a_label_validation_error()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest,
            """{"errors":{"label":["label must be at most 64 characters"]}}"""));
        var client = ClientTestHarness.ClientFor(handler);

        var ex = await Should.ThrowAsync<CrawldadValidationException>(() => client.MintTenantKeyAsync(new string('x', 65)));
        ex.Errors.ContainsKey("label").ShouldBeTrue();
    }

    [Fact]
    public async Task RotateTenantKey_posts_to_the_rotate_path_and_returns_the_replacement()
    {
        var keyId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(
            new TenantApiKeyCreated(Guid.NewGuid(), "ck_test_NEWKEY", "ci", "ck_test_NEWKEY_secret", DateTimeOffset.UtcNow),
            HttpStatusCode.Created));
        var client = ClientTestHarness.ClientFor(handler);

        var rotated = await client.RotateTenantKeyAsync(keyId);

        rotated.ApiKey.ShouldBe("ck_test_NEWKEY_secret");
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe($"/tenant/keys/{keyId}/rotate");
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }

    [Fact]
    public async Task RotateTenantKey_maps_a_missing_key_to_not_found()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.NotFound));
        var client = ClientTestHarness.ClientFor(handler);

        await Should.ThrowAsync<CrawldadNotFoundException>(() => client.RotateTenantKeyAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RevokeTenantKey_sends_a_bearer_delete()
    {
        var keyId = Guid.NewGuid();
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.NoContent));
        var client = ClientTestHarness.ClientFor(handler);

        await client.RevokeTenantKeyAsync(keyId);

        handler.Last.Method.ShouldBe(HttpMethod.Delete);
        handler.Last.Path.ShouldBe($"/tenant/keys/{keyId}");
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }

    [Fact]
    public async Task RevokeTenantKey_maps_the_last_or_current_key_refusal_to_a_409()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.Conflict,
            """{"title":"last_active_key","detail":"cannot revoke the tenant's last active key; rotate it","status":409}"""));
        var client = ClientTestHarness.ClientFor(handler);

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.RevokeTenantKeyAsync(Guid.NewGuid()));
        ex.StatusCode.ShouldBe(409);
        ex.Message.ShouldContain("rotate"); // the portal surfaces this guidance to the user
    }

    [Fact]
    public async Task RevokeTenantKey_maps_a_missing_key_to_not_found()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.NotFound));
        var client = ClientTestHarness.ClientFor(handler);

        await Should.ThrowAsync<CrawldadNotFoundException>(() => client.RevokeTenantKeyAsync(Guid.NewGuid()));
    }
}
