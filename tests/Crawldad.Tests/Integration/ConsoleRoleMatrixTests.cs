using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The console role-enforcement matrix end to end (issue #119 PR6): Owner vs Member vs no-membership across reads,
/// operational writes, key management, and membership management, through the REAL console pipeline (a test-issued token +
/// the appid/role validator + the membership store as authority). The mandate PR#154 flagged: a Member must not be a
/// de-facto Owner. Owner reaches everything; Member reaches reads + operational writes but is a <c>403</c> on key/membership
/// management; a user with no membership is a <c>403</c> everywhere. Key possession (the unrestricted channel) is proven
/// separately in <see cref="MembershipEndpointTests"/>.</summary>
[Collection(ConsoleAuthCollection.Name)]
public sealed class ConsoleRoleMatrixTests(ConsoleAuthFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private static readonly CancellationToken _ct = CancellationToken.None;
    private const string _tenantId = "5c1d2e3f-1111-4a2b-9c3d-0123456789ab";
    private const string _owner = "owner@crawldad.test";
    private const string _member = "member@crawldad.test";
    private const string _stranger = "stranger@crawldad.test";
    private const string _payload = """{ "crawldad": "1", "name": "demo", "config": { "backend": "input.backend" }, "steps": [], "result": "'v1'" }""";

    public async Task InitializeAsync()
    {
        await Host.ResetAllMartenDataAsync();
        await Host.Services.GetRequiredService<ITenantRegistryStore>().CreateAsync(new RegistryTenant
        {
            Id = _tenantId,
            DisplayName = "Role Matrix",
            Actor = _tenantId,
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, _ct);
        var store = Host.Services.GetRequiredService<ITenantMembershipStore>();
        await store.CreateAsync(_tenantId, _owner, MembershipRole.Owner, DateTimeOffset.UnixEpoch, _ct);
        await store.CreateAsync(_tenantId, _member, MembershipRole.Member, DateTimeOffset.UnixEpoch, _ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Reads: both Owner and Member ------------------------------------------------------------------------------

    [Theory]
    [InlineData(_owner)]
    [InlineData(_member)]
    public async Task Any_member_may_read(string user)
    {
        (await StatusAsync(user, "GET", "/tenant")).ShouldBe(StatusCodes.Status200OK);
        (await StatusAsync(user, "GET", "/tenant/memberships")).ShouldBe(StatusCodes.Status200OK);
        (await StatusAsync(user, "GET", "/workspaces")).ShouldBe(StatusCodes.Status200OK);
    }

    // ---- Operational writes: both Owner and Member -----------------------------------------------------------------

    [Theory]
    [InlineData(_owner)]
    [InlineData(_member)]
    public async Task Any_member_may_perform_operational_writes(string user)
    {
        // Drafting a payload is a console write that stays Member-reachable (it is not key/membership management).
        (await StatusAsync(user, "POST", "/payloads", new JsonObject { ["payload"] = JsonNode.Parse(_payload) })).ShouldBe(StatusCodes.Status200OK);
    }

    // ---- Key management: Owner-only --------------------------------------------------------------------------------

    [Fact]
    public async Task Owner_may_manage_keys_but_member_may_not()
    {
        (await StatusAsync(_owner, "POST", "/tenant/keys", new JsonObject())).ShouldBe(StatusCodes.Status201Created);
        (await StatusAsync(_member, "POST", "/tenant/keys", new JsonObject())).ShouldBe(StatusCodes.Status403Forbidden);
        // Rotate/revoke are likewise Owner-only — a Member is refused before the handler (a random id would otherwise 404).
        (await StatusAsync(_member, "POST", $"/tenant/keys/{Guid.NewGuid()}/rotate", new JsonObject())).ShouldBe(StatusCodes.Status403Forbidden);
        (await StatusAsync(_member, "DELETE", $"/tenant/keys/{Guid.NewGuid()}")).ShouldBe(StatusCodes.Status403Forbidden);
    }

    // ---- Membership management: Owner-only -------------------------------------------------------------------------

    [Fact]
    public async Task Member_may_not_manage_memberships()
    {
        (await StatusAsync(_member, "POST", "/tenant/memberships", new JsonObject { ["email"] = "invitee@x.test", ["role"] = "member" })).ShouldBe(StatusCodes.Status403Forbidden);
        (await StatusAsync(_member, "DELETE", $"/tenant/memberships/{Guid.NewGuid()}")).ShouldBe(StatusCodes.Status403Forbidden);
        (await StatusAsync(_member, "POST", $"/tenant/memberships/{Guid.NewGuid()}/role", new JsonObject { ["role"] = "owner" })).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Owner_may_add_change_role_and_remove_a_member()
    {
        // Add a member.
        var added = await JsonAsync(_owner, "POST", "/tenant/memberships", new JsonObject { ["email"] = "invitee@x.test", ["role"] = "member" }, StatusCodes.Status200OK);
        added.GetProperty("role").GetString().ShouldBe("member");
        var membershipId = added.GetProperty("membershipId").GetGuid();

        // Promote to owner, then remove.
        var promoted = await JsonAsync(_owner, "POST", $"/tenant/memberships/{membershipId}/role", new JsonObject { ["role"] = "owner" }, StatusCodes.Status200OK);
        promoted.GetProperty("role").GetString().ShouldBe("owner");
        (await StatusAsync(_owner, "DELETE", $"/tenant/memberships/{membershipId}")).ShouldBe(StatusCodes.Status204NoContent);
    }

    // ---- No membership: 403 everywhere -----------------------------------------------------------------------------

    [Fact]
    public async Task A_user_without_a_membership_is_forbidden_on_reads_and_writes()
    {
        (await StatusAsync(_stranger, "GET", "/tenant")).ShouldBe(StatusCodes.Status403Forbidden);
        (await StatusAsync(_stranger, "POST", "/payloads", new JsonObject { ["payload"] = JsonNode.Parse(_payload) })).ShouldBe(StatusCodes.Status403Forbidden);
        (await StatusAsync(_stranger, "POST", "/tenant/keys", new JsonObject())).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Workspaces_lists_only_the_callers_own_workspaces()
    {
        var body = await JsonAsync(_member, "GET", "/workspaces", body: null, StatusCodes.Status200OK);
        var workspaces = body.GetProperty("workspaces").EnumerateArray().ToList();
        workspaces.Count.ShouldBe(1);
        workspaces[0].GetProperty("tenantId").GetString().ShouldBe(_tenantId);
        workspaces[0].GetProperty("displayName").GetString().ShouldBe("Role Matrix");
        workspaces[0].GetProperty("role").GetString().ShouldBe("member");
    }

    [Fact]
    public async Task Workspaces_skips_a_membership_whose_workspace_no_longer_exists()
    {
        // The owner also holds a membership on a tenant that has no RegistryTenant (deleted/never-provisioned) — GET
        // /workspaces skips it rather than surfacing a dangling row, so only the live workspace comes back.
        await Host.Services.GetRequiredService<ITenantMembershipStore>()
            .CreateAsync("gone-tenant-id", _owner, MembershipRole.Owner, DateTimeOffset.UnixEpoch, _ct);

        var body = await JsonAsync(_owner, "GET", "/workspaces", body: null, StatusCodes.Status200OK);
        var ids = body.GetProperty("workspaces").EnumerateArray().Select(w => w.GetProperty("tenantId").GetString()).ToList();

        ids.ShouldBe([_tenantId]); // the dangling membership on "gone-tenant-id" is filtered out
    }

    private async Task<int> StatusAsync(string user, string method, string path, JsonNode? body = null)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, user);
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, _tenantId);
            Route(x, method, path, body);
            x.IgnoreStatusCode();
        });
        return result.Context.Response.StatusCode;
    }

    private async Task<JsonElement> JsonAsync(string user, string method, string path, JsonNode? body, int expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, user);
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, _tenantId);
            Route(x, method, path, body);
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private static void Route(Scenario x, string method, string path, JsonNode? body)
    {
        switch (method)
        {
            case "GET":
                x.Get.Url(path);
                break;
            case "DELETE":
                x.Delete.Url(path);
                break;
            default:
                x.Post.Json(body ?? new JsonObject()).ToUrl(path);
                break;
        }
    }
}
