using System.Text;
using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The binding, reviewable definition of the console scope — reads (issue #119 PR4) AND writes (PR5), pinned as two
/// INDEPENDENT hand-lists here; the test derives the LIVE admitting-set from endpoint authorization metadata and asserts it
/// equals their union — so a route added to (or dropped from) <see cref="ConsoleReadEndpoints"/> / <see cref="ConsoleWriteEndpoints"/>
/// without updating these lists fails CI as scope creep. It then proves the behaviour both ways: every non-console route
/// rejects a valid console token (<c>401</c>), and every console route (read or write) accepts it once a membership exists
/// (never <c>401</c>/<c>403</c>).</summary>
[Collection(ConsoleAuthCollection.Name)]
public sealed class ConsoleReadEnumerationTests(ConsoleAuthFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private static readonly CancellationToken _ct = CancellationToken.None;
    private const string _tenantId = "7f2b8c40-1111-4a2b-9c3d-0123456789ab";
    private const string _email = "console-enum@crawldad.test";

    // The intended console-read scope — kept independent of ConsoleReadEndpoints.Routes ON PURPOSE, so this test is a real
    // tripwire against the wiring, not a tautology.
    private static readonly HashSet<string> _intendedConsoleReads = new(StringComparer.Ordinal)
    {
        "GET /runs",
        "GET /runs/{id}",
        "GET /runs/{id}/timeline",
        "GET /runs/{id}/drift",
        "GET /runs/{id}/screenshots/{reference}",
        "GET /payloads",
        "GET /payloads/{id}",
        "GET /payloads/{id}/revisions/{revision}",
        "GET /payloads/{id}/diff/{from}/{to}",
        "GET /payloads/{id}/drift-status",
        "GET /webhooks",
        "GET /webhooks/{name}/deliveries",
        "GET /tenant",
        "GET /usage",
        "GET /billing/config",
        "GET /tenant/keys",
        "GET /tenant/memberships",  // a workspace's members — Member-readable (management is Owner-only, below)
        "GET /workspaces",          // the caller's own workspaces — the switcher (issue #119 PR6)
    };

    // The intended console-WRITE scope (PR5) — the writes the portal performs, and only those. Kept independent of
    // ConsoleWriteEndpoints.Routes on purpose (a separate list from the reads above), so this stays a real tripwire.
    private static readonly HashSet<string> _intendedConsoleWrites = new(StringComparer.Ordinal)
    {
        "POST /runs/{id}/replay",
        "PUT /webhooks/{name}",
        "DELETE /webhooks/{name}",
        "POST /payloads",
        "POST /payloads/{id}/revise",
        "POST /billing/checkout-session",
        "POST /billing/portal-session",
        "POST /tenant/keys",
        "POST /tenant/keys/{id}/rotate",
        "DELETE /tenant/keys/{id}",
        "POST /tenant/memberships",
        "DELETE /tenant/memberships/{id}",       // remove a member — Owner-only (issue #119 PR6)
        "POST /tenant/memberships/{id}/role",    // change a member's role — Owner-only (issue #119 PR6)
    };

    // The Owner-only console subset (issue #119 PR6): key + membership management. On the console channel these additionally
    // require the Owner role (ConsoleOwnerOrKey policy); every other console write is Member-reachable (ConsoleOrKey). Kept
    // independent of ConsoleOwnerEndpoints.Routes on purpose, so this is a real tripwire against the wiring.
    private static readonly HashSet<string> _intendedConsoleOwnerScope = new(StringComparer.Ordinal)
    {
        "POST /tenant/keys",
        "POST /tenant/keys/{id}/rotate",
        "DELETE /tenant/keys/{id}",
        "POST /tenant/memberships",
        "DELETE /tenant/memberships/{id}",
        "POST /tenant/memberships/{id}/role",
    };

    // The full console scope: reads + writes. A route accepts a console principal iff it is in one of these lists.
    private static readonly HashSet<string> _intendedConsoleScope =
        new(_intendedConsoleReads.Concat(_intendedConsoleWrites), StringComparer.Ordinal);

    // Anonymous, tenant-independent routes (identical to AuthenticationTests) — never console-reachable, never asserted.
    private static readonly HashSet<string> _anonymousRoutes = new(StringComparer.Ordinal)
    {
        "/health", "/schema/crawldad-1.schema.json", "/llms.txt", "/openapi.json", "/billing/webhook",
    };

    public async Task InitializeAsync()
    {
        await Host.ResetAllMartenDataAsync();
        await Host.Services.GetRequiredService<ITenantRegistryStore>().CreateAsync(new RegistryTenant
        {
            Id = _tenantId,
            DisplayName = "Console Enum",
            Actor = _tenantId,
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, _ct);
        await Host.Services.GetRequiredService<ITenantMembershipStore>().CreateOwnerAsync(_tenantId, _email, DateTimeOffset.UnixEpoch, _ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void The_live_console_admitting_set_equals_the_intended_enumeration()
    {
        // A route admits a console principal under EITHER console policy (ConsoleOrKey for Member-reachable reads/writes,
        // ConsoleOwnerOrKey for the Owner-only subset). The union must equal the whole intended console scope.
        var admitting = RoutesCarryingPolicy(
            ConsoleAuthModule.ConsoleOrKeyPolicy,
            ConsoleAuthModule.ConsoleOwnerOrKeyPolicy);

        admitting.ShouldBe(_intendedConsoleScope, ignoreOrder: true); // exactly the reads + writes, nothing more
    }

    [Fact]
    public void The_live_owner_only_admitting_set_equals_the_intended_enumeration()
    {
        // Exactly the Owner-only subset carries the stricter ConsoleOwnerOrKey policy — a mis-scoped route (a Member-reachable
        // write wrongly Owner-gated, or an Owner-only write left Member-reachable) fails CI here.
        var ownerScope = RoutesCarryingPolicy(ConsoleAuthModule.ConsoleOwnerOrKeyPolicy);

        ownerScope.ShouldBe(_intendedConsoleOwnerScope, ignoreOrder: true);
    }

    private HashSet<string> RoutesCarryingPolicy(params string[] policies)
    {
        var admitting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in RoutableEndpoints())
        {
            var carries = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => policies.Any(policy => string.Equals(data.Policy, policy, StringComparison.Ordinal)));
            if (carries)
            {
                admitting.Add($"{Method(endpoint)} {endpoint.RoutePattern.RawText}");
            }
        }

        return admitting;
    }

    [Fact]
    public async Task Every_non_console_route_rejects_a_console_token()
    {
        var offenders = new List<string>();
        foreach (var endpoint in RoutableEndpoints())
        {
            var route = endpoint.RoutePattern.RawText!;
            var path = BuildProbePath(endpoint.RoutePattern);
            if (_anonymousRoutes.Contains(path) || _intendedConsoleScope.Contains($"{Method(endpoint)} {route}"))
            {
                continue; // anonymous, or a curated console read/write route (asserted positively below)
            }

            var status = await ConsoleTokenStatusAsync(Method(endpoint), path);
            if (status != StatusCodes.Status401Unauthorized)
            {
                offenders.Add($"{Method(endpoint)} {path} → {status}");
            }
        }

        offenders.ShouldBeEmpty($"these non-console routes did not reject a console token with 401: {string.Join(", ", offenders)}");
    }

    [Fact]
    public async Task Every_console_route_accepts_a_console_token_with_a_membership()
    {
        var offenders = new List<string>();
        foreach (var endpoint in RoutableEndpoints())
        {
            if (!_intendedConsoleScope.Contains($"{Method(endpoint)} {endpoint.RoutePattern.RawText}"))
            {
                continue;
            }

            // A valid console token + the seeded (email, workspace) membership authorizes the request (read OR write): the
            // resource may be a 200/201/204 or a 4xx/503 (a random id, an empty body, or billing unconfigured), but
            // authentication/authorization has passed — never 401/403.
            var status = await ConsoleTokenStatusAsync(Method(endpoint), BuildProbePath(endpoint.RoutePattern));
            if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
            {
                offenders.Add($"{Method(endpoint)} {endpoint.RoutePattern.RawText} → {status}");
            }
        }

        offenders.ShouldBeEmpty($"these console routes rejected an authorized console token: {string.Join(", ", offenders)}");
    }

    private List<RouteEndpoint> RoutableEndpoints() =>
        [.. Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null)];

    private static string Method(RouteEndpoint endpoint) => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0];

    private async Task<int> ConsoleTokenStatusAsync(string method, string path)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, _email);
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, _tenantId);
            Route(x, method, path);
            x.IgnoreStatusCode();
        });
        return result.Context.Response.StatusCode;
    }

    private static void Route(Scenario x, string method, string path)
    {
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            x.Get.Url(path);
        }
        else if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase))
        {
            x.Put.Json(new { }).ToUrl(path);
        }
        else if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            x.Delete.Url(path);
        }
        else
        {
            x.Post.Json(new { }).ToUrl(path);
        }
    }

    private static string BuildProbePath(RoutePattern pattern)
    {
        var builder = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            builder.Append('/');
            foreach (var part in segment.Parts)
            {
                builder.Append(part switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternParameterPart parameter => parameter.ParameterPolicies.Any(policy => string.Equals(policy.Content, "int", StringComparison.Ordinal))
                        ? "1"
                        : Guid.NewGuid().ToString(),
                    _ => "",
                });
            }
        }

        return builder.ToString();
    }
}
