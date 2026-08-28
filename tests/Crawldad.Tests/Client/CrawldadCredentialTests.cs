using Crawldad.Client;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Tests.Client;

/// <summary>The pluggable SDK credential (issue #119): the API-key case (back-compatible with every existing caller), the
/// portal's first-party console case (bearer token + the two selector headers), and the delegate test fake. Also pins the
/// options relaxation — a client authenticates with an <see cref="CrawldadClientOptions.ApiKey"/> OR an explicit
/// <see cref="CrawldadClientOptions.Credential"/> — and that <see cref="CrawldadClient"/> applies the credential per
/// request.</summary>
public class CrawldadCredentialTests
{
    private static async Task<HttpRequestMessage> AppliedAsync(ICrawldadCredential credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.crawldad.test/runs");
        await credential.ApplyAsync(request, CancellationToken.None);
        return request;
    }

    [Fact]
    public async Task ApiKeyCredential_stamps_the_bearer_key()
    {
        var request = await AppliedAsync(new ApiKeyCredential("sk_test_123"));

        request.Headers.Authorization!.ToString().ShouldBe("Bearer sk_test_123");
    }

    [Fact]
    public void ApiKeyCredential_rejects_a_blank_key() =>
        Should.Throw<ArgumentException>(() => new ApiKeyCredential(" "));

    [Fact]
    public async Task ConsoleCredential_stamps_the_bearer_token_and_both_selectors()
    {
        var credential = new ConsoleCredential(_ => ValueTask.FromResult("entra-token"), "user@x.test", "workspace-1");

        var request = await AppliedAsync(credential);

        request.Headers.Authorization!.ToString().ShouldBe("Bearer entra-token");
        request.Headers.GetValues(ConsoleAuthHeaders.ConsoleUser).ShouldBe(["user@x.test"]);
        request.Headers.GetValues(ConsoleAuthHeaders.Workspace).ShouldBe(["workspace-1"]);
    }

    [Fact]
    public void ConsoleCredential_rejects_bad_arguments()
    {
        Should.Throw<ArgumentNullException>(() => new ConsoleCredential(null!, "u", "w"));
        Should.Throw<ArgumentException>(() => new ConsoleCredential(_ => ValueTask.FromResult("t"), " ", "w"));
        Should.Throw<ArgumentException>(() => new ConsoleCredential(_ => ValueTask.FromResult("t"), "u", " "));
    }

    [Fact]
    public async Task ForProvisioning_stamps_the_token_and_user_but_no_workspace_selector()
    {
        // The pre-workspace provisioning credential (issue #119 PR7): token + acting user, but NO X-Crawldad-Workspace
        // (there is no workspace yet), so it is valid only for POST /provisioning/tenants.
        var credential = ConsoleCredential.ForProvisioning(_ => ValueTask.FromResult("entra-token"), "user@x.test");

        var request = await AppliedAsync(credential);

        request.Headers.Authorization!.ToString().ShouldBe("Bearer entra-token");
        request.Headers.GetValues(ConsoleAuthHeaders.ConsoleUser).ShouldBe(["user@x.test"]);
        request.Headers.Contains(ConsoleAuthHeaders.Workspace).ShouldBeFalse();
    }

    [Fact]
    public void ForProvisioning_rejects_bad_arguments()
    {
        Should.Throw<ArgumentNullException>(() => ConsoleCredential.ForProvisioning(null!, "u"));
        Should.Throw<ArgumentException>(() => ConsoleCredential.ForProvisioning(_ => ValueTask.FromResult("t"), " "));
    }

    [Fact]
    public async Task DelegateCredential_defers_to_the_delegate()
    {
        var credential = new DelegateCredential((request, _) =>
        {
            request.Headers.TryAddWithoutValidation("X-Test", "stamped");
            return ValueTask.CompletedTask;
        });

        var request = await AppliedAsync(credential);

        request.Headers.GetValues("X-Test").ShouldBe(["stamped"]);
    }

    [Fact]
    public void DelegateCredential_rejects_a_null_delegate() =>
        Should.Throw<ArgumentNullException>(() => new DelegateCredential(null!));

    [Fact]
    public void Options_validate_accepts_an_explicit_credential_without_an_api_key()
    {
        var options = new CrawldadClientOptions { BaseUrl = new Uri("https://api.crawldad.test/"), Credential = new ApiKeyCredential("k") };

        options.Validate().AbsoluteUri.ShouldBe("https://api.crawldad.test/"); // no ApiKey required when a credential is set
    }

    [Fact]
    public void Options_validate_requires_a_key_or_a_credential()
    {
        var options = new CrawldadClientOptions { BaseUrl = new Uri("https://api.crawldad.test/") };

        Should.Throw<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Constructor_requires_a_key_or_a_credential()
    {
        using var http = new HttpClient();

        Should.Throw<InvalidOperationException>(() => new CrawldadClient(http, new CrawldadClientOptions { BaseUrl = new Uri("https://api.crawldad.test/") }));
    }

    [Fact]
    public async Task Client_applies_the_explicit_credential_per_request()
    {
        // An explicit credential wins over ApiKey and is applied on the actual outbound request.
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(0, 0, 0)));
        using var http = new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl };
        var credential = new DelegateCredential((request, _) =>
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer SENTINEL");
            return ValueTask.CompletedTask;
        });
        var client = new CrawldadClient(http, new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, Credential = credential });

        await client.GetQueueStatsAsync();

        handler.Last.Authorization.ShouldBe("Bearer SENTINEL");
    }

    [Fact]
    public async Task Client_wraps_a_bare_api_key_for_back_compat()
    {
        // The historical shape — options with just an ApiKey — still stamps Authorization: Bearer <key>.
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(0, 0, 0)));
        var client = ClientTestHarness.ClientFor(handler);

        await client.GetQueueStatsAsync();

        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }
}
