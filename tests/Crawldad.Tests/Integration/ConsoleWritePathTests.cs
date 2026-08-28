using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Payloads;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The console-WRITE path end-to-end (issue #119 PR5), on the console-enabled host. A console-authenticated write
/// stamps the human email as the event actor, is recorded in the audit trail (a read and a programmatic key write are not),
/// and a console owner can revoke the tenant's last key (revoke-ALL) because the console is the recovery path.</summary>
[Collection(ConsoleAuthCollection.Name)]
public sealed class ConsoleWritePathTests(ConsoleAuthFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private static readonly CancellationToken _ct = CancellationToken.None;
    private const string _email = "writer@crawldad.test";
    private const string _payload = """{ "crawldad": "1", "name": "demo", "config": { "backend": "input.backend" }, "steps": [], "result": "'v1'" }""";

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewTenantId() => Guid.NewGuid().ToString();

    private async Task SeedTenantAsync(string tenantId) =>
        await Host.Services.GetRequiredService<ITenantRegistryStore>().CreateAsync(new RegistryTenant
        {
            Id = tenantId,
            DisplayName = "Writer Co",
            Actor = tenantId, // the KEY-path actor; the console path stamps the email instead
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, _ct);

    private async Task SeedOwnerAsync(string tenantId, string email) =>
        await Host.Services.GetRequiredService<ITenantMembershipStore>().CreateOwnerAsync(tenantId, email, DateTimeOffset.UnixEpoch, _ct);

    private IConsoleAuditStore Audit => Host.Services.GetRequiredService<IConsoleAuditStore>();

    private async Task<IScenarioResult> ConsoleAsync(string email, string workspace, Action<Scenario> route) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, email);
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, workspace);
            route(x);
            x.IgnoreStatusCode();
        });

    [Fact]
    public async Task A_console_write_stamps_the_membership_email_as_the_event_actor()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId);
        await SeedOwnerAsync(tenantId, _email);

        // Draft a payload, then revise it — both via the console path.
        var draft = await ConsoleAsync(_email, tenantId, x => x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(_payload) }).ToUrl("/payloads"));
        draft.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var payloadId = (await draft.ReadAsJsonAsync<JsonElement>()).GetProperty("payloadId").GetGuid();

        var revise = await ConsoleAsync(_email, tenantId, x => x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(_payload) }).ToUrl($"/payloads/{payloadId}/revise"));
        revise.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        // Ground truth: the event stream carries the human email as the actor on BOTH the draft and the revision — not the
        // tenant's key-path actor (the tenant GUID).
        await using var session = Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);
        var events = await session.Events.FetchStreamAsync(payloadId);
        ((PayloadDrafted)events[0].Data).By.ShouldBe(_email);
        ((PayloadRevised)events[1].Data).By.ShouldBe(_email);
    }

    [Fact]
    public async Task A_console_write_is_audited_with_the_actor_operation_and_route()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId);
        await SeedOwnerAsync(tenantId, _email);

        var draft = await ConsoleAsync(_email, tenantId, x => x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(_payload) }).ToUrl("/payloads"));
        draft.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var rows = await Audit.ListForTenantAsync(tenantId, _ct);
        var row = rows.ShouldHaveSingleItem();
        row.TenantId.ShouldBe(tenantId);
        row.Email.ShouldBe(_email);     // the human email, not the shared portal identity
        row.Operation.ShouldBe("POST");
        row.Route.ShouldBe("/payloads"); // the route template, no bodies/secrets
        row.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task A_console_read_is_not_audited()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId);
        await SeedOwnerAsync(tenantId, _email);

        var read = await ConsoleAsync(_email, tenantId, x => x.Get.Url("/tenant"));
        read.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        (await Audit.ListForTenantAsync(tenantId, _ct)).ShouldBeEmpty(); // audit is for console WRITES only
    }

    [Fact]
    public async Task A_programmatic_key_write_is_not_audited()
    {
        // A write on the SAME endpoint but authenticated with a tenant key (the env primary) is not a console write, so it is
        // never audited — attribution follows the console channel alone.
        var write = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(_payload) }).ToUrl("/payloads");
            x.StatusCodeShouldBe(StatusCodes.Status200OK);
        });
        write.Context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        (await Audit.ListForTenantAsync(TestTenants.PrimaryId, _ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_console_owner_can_revoke_the_tenants_last_key_and_the_delete_is_audited()
    {
        var tenantId = NewTenantId();
        await SeedTenantAsync(tenantId);
        await SeedOwnerAsync(tenantId, _email); // the console recovery path — so revoking the last key is allowed

        // Seed the tenant's ONLY active key directly.
        var registry = Host.Services.GetRequiredService<ITenantRegistryStore>();
        var keyId = Guid.NewGuid();
        await registry.AddKeyAsync(new TenantApiKey
        {
            Id = keyId,
            TenantId = tenantId,
            KeyHash = ApiKeyMint.Hash($"ck_test_{Guid.NewGuid():N}"),
            Prefix = "ck_test",
            CreatedAt = DateTimeOffset.UnixEpoch,
        }, _ct);

        // The console presents no API key, so it is never the "current" key — the owner can revoke the last one.
        var delete = await ConsoleAsync(_email, tenantId, x => x.Delete.Url($"/tenant/keys/{keyId}"));
        delete.Context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);

        // The key is now revoked (zero active keys — recoverable via the console), and the DELETE was audited.
        var keys = await registry.ListKeysAsync(tenantId, _ct);
        keys.ShouldHaveSingleItem().RevokedAt.ShouldNotBeNull();

        var row = (await Audit.ListForTenantAsync(tenantId, _ct)).ShouldHaveSingleItem();
        row.Operation.ShouldBe("DELETE");
        row.Route.ShouldBe("/tenant/keys/{id}");
        row.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
    }
}
