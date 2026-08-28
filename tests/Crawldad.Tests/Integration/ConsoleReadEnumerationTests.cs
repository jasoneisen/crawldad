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

/// <summary>The binding, reviewable definition of the console READ scope (issue #119 PR4). The intended set is an
/// INDEPENDENT hand-list here; the test derives the LIVE admitting-set from endpoint authorization metadata and asserts
/// they are exactly equal — so a route added to (or dropped from) <see cref="ConsoleReadEndpoints"/> without updating this
/// list fails CI as scope creep. It then proves the behaviour both ways: every non-console route rejects a valid console
/// token (<c>401</c>), and every console route accepts it once a membership exists (never <c>401</c>/<c>403</c>).</summary>
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
    };

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
        var admitting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in RoutableEndpoints())
        {
            var carriesConsolePolicy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => string.Equals(data.Policy, ConsoleAuthModule.ConsoleOrKeyPolicy, StringComparison.Ordinal));
            if (carriesConsolePolicy)
            {
                admitting.Add($"{Method(endpoint)} {endpoint.RoutePattern.RawText}");
            }
        }

        admitting.ShouldBe(_intendedConsoleReads, ignoreOrder: true);
    }

    [Fact]
    public async Task Every_non_console_route_rejects_a_console_token()
    {
        var offenders = new List<string>();
        foreach (var endpoint in RoutableEndpoints())
        {
            var route = endpoint.RoutePattern.RawText!;
            var path = BuildProbePath(endpoint.RoutePattern);
            if (_anonymousRoutes.Contains(path) || _intendedConsoleReads.Contains($"{Method(endpoint)} {route}"))
            {
                continue; // anonymous, or a curated console-read route (asserted positively below)
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
            if (!_intendedConsoleReads.Contains($"{Method(endpoint)} {endpoint.RoutePattern.RawText}"))
            {
                continue;
            }

            // A valid console token + the seeded (email, workspace) membership authorizes the read: the resource may be a
            // 200 or a 404 (a random path-param id), but authentication/authorization has passed — never 401/403.
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
