using System.Text.Json;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Webhooks;
using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The webhooks registration API end to end (PUT/GET/DELETE) plus the store's encryption-at-rest: registration
/// returns metadata and never the secret, listings omit secrets, the stored document holds ciphertext, a re-registration
/// preserves createdAt while advancing updatedAt, the SSRF/validation guards reject bad input, and one tenant can never
/// see or delete another's registrations.</summary>
[Collection(WebhookApiCollection.Name)]
public sealed class WebhookEndpointTests(WebhookApiFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private IAlbaHost Host => fixture.Host;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> RegisterAsync(string name, object body, string apiKey = TestTenants.PrimaryKey)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Put.Json(body).ToUrl($"/webhooks/{name}");
            x.StatusCodeShouldBe(200);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Registers_and_returns_metadata_without_the_secret()
    {
        var json = await RegisterAsync("prod", new { url = "https://hooks.example.com/prod", secret = "whsec_LEAKME_0123456789", events = new[] { "run.failed" } });

        json.GetProperty("name").GetString().ShouldBe("prod");
        json.GetProperty("url").GetString().ShouldBe("https://hooks.example.com/prod");
        json.GetProperty("events").EnumerateArray().Select(e => e.GetString()).ShouldBe(["run.failed"]);
        json.TryGetProperty("createdAt", out _).ShouldBeTrue();
        json.TryGetProperty("updatedAt", out _).ShouldBeTrue();

        json.GetRawText().ShouldNotContain("whsec_LEAKME_0123456789"); // the secret value never appears
        json.TryGetProperty("secret", out _).ShouldBeFalse();
        json.TryGetProperty("protectedSecret", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Lists_registered_sorted_and_without_secrets()
    {
        await RegisterAsync("beta", new { url = "https://hooks.example.com/beta", secret = "whsec_BETA_0123456789" });
        await RegisterAsync("alpha", new { url = "https://hooks.example.com/alpha", secret = "whsec_ALPHA_0123456789" });

        var json = await ListAsync();

        var names = json.GetProperty("webhooks").EnumerateArray().Select(w => w.GetProperty("name").GetString()).ToList();
        names.ShouldBe(["alpha", "beta"]); // ordered by name

        var raw = json.GetRawText();
        raw.ShouldNotContain("whsec_ALPHA_0123456789");
        raw.ShouldNotContain("whsec_BETA_0123456789");
        foreach (var webhook in json.GetProperty("webhooks").EnumerateArray())
        {
            webhook.TryGetProperty("secret", out _).ShouldBeFalse();
            webhook.TryGetProperty("protectedSecret", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task An_empty_events_list_means_all_and_is_listed_as_such()
    {
        await RegisterAsync("all", new { url = "https://hooks.example.com/all", secret = "whsec_value_0123456789" });

        var webhook = (await ListAsync()).GetProperty("webhooks")[0];
        webhook.GetProperty("events").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task The_stored_document_holds_ciphertext_and_round_trips()
    {
        await RegisterAsync("prod", new { url = "https://hooks.example.com/prod", secret = "PLAINTEXT_secret_at_rest_xyz" });

        var docStore = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = docStore.QuerySession(TestTenants.PrimaryId);
        var doc = await session.LoadAsync<WebhookEndpoint>("prod", _ct);

        doc.ShouldNotBeNull();
        doc.ProtectedSecret.ShouldNotBe("PLAINTEXT_secret_at_rest_xyz");
        doc.ProtectedSecret.ShouldNotContain("PLAINTEXT_secret_at_rest_xyz");

        var store = Host.Services.GetRequiredService<IWebhookEndpointStore>();
        var resolved = await store.ResolveAsync(session, "prod", _ct);
        resolved.ShouldNotBeNull();
        resolved.Secret.ShouldBe("PLAINTEXT_secret_at_rest_xyz"); // decrypts back for signing
    }

    [Fact]
    public async Task A_re_registration_replaces_metadata_and_preserves_createdAt()
    {
        var docStore = Host.Services.GetRequiredService<IDocumentStore>();
        var protection = Host.Services.GetRequiredService<IDataProtectionProvider>();
        var clock = new MutableClock(FakeClock.Fixed);
        var store = new MartenWebhookEndpointStore(docStore, protection, clock);

        var created = await store.RegisterAsync(TestTenants.PrimaryId, "prod", "https://hooks.example.com/a", "secret-value-1-0123", [], _ct);
        clock.Now = FakeClock.Fixed.AddMinutes(5);
        var updated = await store.RegisterAsync(TestTenants.PrimaryId, "prod", "https://hooks.example.com/b", "secret-value-2-0123", ["run.succeeded"], _ct);

        updated.CreatedAt.ShouldBe(created.CreatedAt);
        updated.UpdatedAt.ShouldBe(FakeClock.Fixed.AddMinutes(5));
        updated.Url.ShouldBe("https://hooks.example.com/b");
        updated.Events.ShouldBe(["run.succeeded"]);
    }

    [Fact]
    public async Task Deletes_a_registration()
    {
        await RegisterAsync("prod", new { url = "https://hooks.example.com/prod", secret = "whsec_value_0123456789" });

        await Host.Scenario(x =>
        {
            x.Delete.Url("/webhooks/prod");
            x.StatusCodeShouldBe(204);
        });

        (await ListAsync()).GetProperty("webhooks").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_an_unknown_webhook_is_a_404() =>
        await Host.Scenario(x =>
        {
            x.Delete.Url("/webhooks/ghost");
            x.StatusCodeShouldBe(404);
        });

    [Theory]
    [InlineData("Bad_Name")]
    [InlineData("has space")]
    public async Task An_invalid_name_is_a_400(string name) =>
        await Host.Scenario(x =>
        {
            x.Put.Json(new { url = "https://hooks.example.com/x", secret = "whsec_value_0123456789" }).ToUrl($"/webhooks/{name}");
            x.StatusCodeShouldBe(400);
        });

    [Theory]
    [InlineData("http://hooks.example.com/x")]  // not https
    [InlineData("https://127.0.0.1/x")]         // loopback
    [InlineData("https://10.0.0.1/x")]          // private
    [InlineData("https://169.254.169.254/x")]   // link-local (cloud metadata)
    public async Task A_disallowed_url_is_a_400(string target) =>
        await Host.Scenario(x =>
        {
            x.Put.Json(new { url = target, secret = "whsec_value_0123456789" }).ToUrl("/webhooks/prod");
            x.StatusCodeShouldBe(400);
        });

    [Fact]
    public async Task A_short_secret_is_a_400() =>
        await Host.Scenario(x =>
        {
            x.Put.Json(new { url = "https://hooks.example.com/x", secret = "tooshort" }).ToUrl("/webhooks/prod");
            x.StatusCodeShouldBe(400);
        });

    [Fact]
    public async Task An_unknown_event_is_a_400() =>
        await Host.Scenario(x =>
        {
            x.Put.Json(new { url = "https://hooks.example.com/x", secret = "whsec_value_0123456789", events = new[] { "run.exploded" } }).ToUrl("/webhooks/prod");
            x.StatusCodeShouldBe(400);
        });

    [Fact]
    public async Task A_tenant_never_sees_or_deletes_anothers_registrations()
    {
        await RegisterAsync("shared-name", new { url = "https://hooks.example.com/x", secret = "whsec_value_0123456789" });

        // The secondary tenant sees none of the primary's registrations.
        var secondaryList = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.SecondaryKey));
            x.Get.Url("/webhooks");
            x.StatusCodeShouldBe(200);
        });
        (await secondaryList.ReadAsJsonAsync<JsonElement>()).GetProperty("webhooks").GetArrayLength().ShouldBe(0);

        // And a cross-tenant delete is a plain 404 (no existence oracle).
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.SecondaryKey));
            x.Delete.Url("/webhooks/shared-name");
            x.StatusCodeShouldBe(404);
        });
    }

    private async Task<JsonElement> ListAsync()
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Get.Url("/webhooks");
            x.StatusCodeShouldBe(200);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }
}
