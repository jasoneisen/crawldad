using System.Text.Json;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Browsers;
using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The browsers registration API end to end (PUT/GET/DELETE) plus the store's encryption-at-rest: registration
/// returns metadata and never the secret, listings omit secrets, the stored document holds ciphertext, and a
/// re-registration preserves createdAt while advancing updatedAt.</summary>
[Collection(BrowserApiCollection.Name)]
public sealed class BrowserEndpointTests(BrowserApiFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private IAlbaHost Host => fixture.Host;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync(); // isolate each test on the shared host
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> RegisterAsync(string name, object body, string apiKey = TestTenants.PrimaryKey)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Put.Json(body).ToUrl($"/browsers/{name}");
            x.StatusCodeShouldBe(200);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Registers_and_returns_metadata_without_the_secret()
    {
        var json = await RegisterAsync("prod", new { adapter = "browserbase", mode = "apiKey", secret = "bb_live_LEAKME_abc123" });

        json.GetProperty("name").GetString().ShouldBe("prod");
        json.GetProperty("adapter").GetString().ShouldBe("browserbase");
        json.GetProperty("mode").GetString().ShouldBe("apiKey");
        json.TryGetProperty("createdAt", out _).ShouldBeTrue();
        json.TryGetProperty("updatedAt", out _).ShouldBeTrue();

        json.GetRawText().ShouldNotContain("bb_live_LEAKME_abc123"); // the secret value never appears
        json.TryGetProperty("secret", out _).ShouldBeFalse();        // no secret property in the response
        json.TryGetProperty("protectedSecret", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Lists_registered_browsers_sorted_and_without_secrets()
    {
        await RegisterAsync("beta", new { adapter = "browserless", mode = "apiKey", secret = "tok_BETA_secret_value" });
        await RegisterAsync("alpha", new { adapter = "browserbase", mode = "apiKey", secret = "tok_ALPHA_secret_value" });

        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Get.Url("/browsers");
            x.StatusCodeShouldBe(200);
        });
        var json = await result.ReadAsJsonAsync<JsonElement>();

        var names = json.GetProperty("browsers").EnumerateArray().Select(b => b.GetProperty("name").GetString()).ToList();
        names.ShouldBe(["alpha", "beta"]); // ordered by name

        var raw = json.GetRawText();
        raw.ShouldNotContain("tok_ALPHA_secret_value");
        raw.ShouldNotContain("tok_BETA_secret_value");
        foreach (var b in json.GetProperty("browsers").EnumerateArray())
        {
            b.TryGetProperty("secret", out _).ShouldBeFalse();
            b.TryGetProperty("protectedSecret", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Stores_and_lists_options()
    {
        await RegisterAsync("prod", new
        {
            adapter = "browserless",
            mode = "apiKey",
            secret = "tok_value_123",
            options = new { region = "sfo", projectId = "p-42" },
        });

        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Get.Url("/browsers");
            x.StatusCodeShouldBe(200);
        });
        var json = await result.ReadAsJsonAsync<JsonElement>();
        var options = json.GetProperty("browsers")[0].GetProperty("options");
        options.GetProperty("region").GetString().ShouldBe("sfo");
        options.GetProperty("projectId").GetString().ShouldBe("p-42");
    }

    [Fact]
    public async Task The_stored_document_holds_ciphertext_and_round_trips()
    {
        await RegisterAsync("prod", new { adapter = "browserbase", mode = "apiKey", secret = "PLAINTEXT_at_rest_xyz" });

        var docStore = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = docStore.QuerySession(TestTenants.PrimaryId);
        var doc = await session.LoadAsync<BrowserRegistration>("prod", _ct);

        doc.ShouldNotBeNull();
        doc.ProtectedSecret.ShouldNotBe("PLAINTEXT_at_rest_xyz");        // encrypted, not plaintext
        doc.ProtectedSecret.ShouldNotContain("PLAINTEXT_at_rest_xyz");

        var store = Host.Services.GetRequiredService<IBrowserCredentialStore>();
        (await store.TryResolveSecretAsync(TestTenants.PrimaryId, "prod", _ct)).ShouldBe("PLAINTEXT_at_rest_xyz"); // decrypts back
    }

    [Fact]
    public async Task A_re_registration_replaces_metadata_and_secret_and_preserves_createdAt()
    {
        var docStore = Host.Services.GetRequiredService<IDocumentStore>();
        var protection = Host.Services.GetRequiredService<IDataProtectionProvider>();
        var clock = new MutableClock(FakeClock.Fixed);
        var store = new MartenBrowserCredentialStore(docStore, protection, clock);

        var created = await store.RegisterAsync(TestTenants.PrimaryId, "prod", "browserbase", "apiKey", "secret-1", null, _ct);
        clock.Now = FakeClock.Fixed.AddMinutes(5);
        var updated = await store.RegisterAsync(TestTenants.PrimaryId, "prod", "browserless", "apiKey", "secret-2", null, _ct);

        updated.CreatedAt.ShouldBe(created.CreatedAt);       // preserved across the update
        updated.UpdatedAt.ShouldBe(FakeClock.Fixed.AddMinutes(5)); // advanced
        updated.Adapter.ShouldBe("browserless");             // metadata replaced
        (await store.TryResolveSecretAsync(TestTenants.PrimaryId, "prod", _ct)).ShouldBe("secret-2"); // new secret
    }

    [Fact]
    public async Task Deletes_a_registration()
    {
        await RegisterAsync("prod", new { adapter = "browserbase", mode = "apiKey", secret = "tok_value_123" });

        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Delete.Url("/browsers/prod");
            x.StatusCodeShouldBe(204);
        });

        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Get.Url("/browsers");
            x.StatusCodeShouldBe(200);
        });
        var json = await result.ReadAsJsonAsync<JsonElement>();
        json.GetProperty("browsers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_an_unknown_browser_is_a_404() =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Delete.Url("/browsers/ghost");
            x.StatusCodeShouldBe(404);
        });

    [Theory]
    [InlineData("Bad_Name")]     // invalid slug (uppercase + underscore)
    [InlineData("has space")]
    public async Task An_invalid_name_is_a_400(string name) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Put.Json(new { adapter = "browserbase", mode = "apiKey", secret = "tok_value_123" }).ToUrl($"/browsers/{name}");
            x.StatusCodeShouldBe(400);
        });

    [Fact]
    public async Task An_unknown_adapter_is_a_400() =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Put.Json(new { adapter = "selenium", mode = "apiKey", secret = "tok_value_123" }).ToUrl("/browsers/prod");
            x.StatusCodeShouldBe(400);
        });

    [Fact]
    public async Task A_connectUrl_secret_that_is_not_a_url_is_a_400() =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Put.Json(new { adapter = "browserbase", mode = "connectUrl", secret = "not-a-url" }).ToUrl("/browsers/prod");
            x.StatusCodeShouldBe(400);
        });
}
