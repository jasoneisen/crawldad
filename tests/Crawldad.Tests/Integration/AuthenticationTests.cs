using System.Text;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The authenticated-tenant boundary (CD-1, §12): every route requires a valid per-tenant API key, presented as
/// <c>Authorization: Bearer</c> or <c>X-Api-Key</c>. The headline gate is <b>enumerated</b> — it discovers the app's routes
/// from the live endpoint table and asserts each rejects an unauthenticated request, so a future route added without auth
/// FAILS this test by default rather than escaping a hand-maintained list. The one intentional exception is the anonymous
/// liveness probe (<c>/health</c>), asserted reachable without a key.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuthenticationTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    // The routes that are intentionally anonymous. A new anonymous route must be added here deliberately — otherwise the
    // enumeration below fails, which is the point (no route escapes the tenant gate by accident). Besides the /health
    // liveness probe, the #20/#21 docs surface is anonymous by design: the payload JSON Schema, the llms.txt discovery index,
    // and the #21 OpenAPI envelope description are public, tenant-independent product artifacts (no tenant data), only useful
    // when reachable without a key — the same deliberate opt-out as /health, decided in SchemaEndpoint / LlmsEndpoint / OpenApiEndpoint.
    private static readonly HashSet<string> _anonymousRoutes = new(StringComparer.Ordinal)
    {
        "/health",
        "/schema/crawldad-1.schema.json",
        "/llms.txt",
        "/openapi.json",
    };

    [Fact]
    public async Task Every_non_anonymous_route_rejects_an_unauthenticated_request()
    {
        var endpoints = Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null)
            .ToList();

        endpoints.Count.ShouldBeGreaterThan(5); // sanity: the enumeration actually found the app's routes

        var offenders = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var path = BuildProbePath(endpoint.RoutePattern);
            if (_anonymousRoutes.Contains(path))
            {
                continue;
            }

            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0];
            var status = await UnauthenticatedStatusAsync(method, path);
            if (status != StatusCodes.Status401Unauthorized)
            {
                offenders.Add($"{method} {path} → {status}");
            }
        }

        offenders.ShouldBeEmpty($"these routes did not require authentication: {string.Join(", ", offenders)}");
    }

    [Fact]
    public async Task Health_is_reachable_without_authentication() =>
        await Host.Scenario(x =>
        {
            x.RemoveRequestHeader("Authorization"); // drop the fixture's default key — health must answer anonymously
            x.Get.Url("/health");
            x.StatusCodeShouldBeOk();
        });

    [Fact]
    public async Task A_valid_x_api_key_header_authenticates() =>
        await Host.Scenario(x =>
        {
            x.RemoveRequestHeader("Authorization");
            x.WithRequestHeader(CrawldadAuthentication.ApiKeyHeader, TestTenants.PrimaryKey);
            x.Get.Url("/payloads");
            x.StatusCodeShouldBeOk();
        });

    [Fact]
    public async Task An_unknown_api_key_is_rejected() =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer("not-a-configured-key-xxxxxx"));
            x.Get.Url("/payloads");
            x.StatusCodeShouldBe(StatusCodes.Status401Unauthorized);
        });

    [Fact]
    public async Task An_empty_bearer_value_is_rejected() =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", "Bearer "); // present header, no key → unauthenticated
            x.Get.Url("/payloads");
            x.StatusCodeShouldBe(StatusCodes.Status401Unauthorized);
        });

    private async Task<int> UnauthenticatedStatusAsync(string method, string path)
    {
        var result = await Host.Scenario(x =>
        {
            x.RemoveRequestHeader("Authorization"); // strip the fixture's default key so the request is unauthenticated
            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                x.Get.Url(path);
            }
            else
            {
                x.Post.Json(new { }).ToUrl(path); // body is irrelevant — authorization rejects before model binding
            }

            x.IgnoreStatusCode();
        });
        return result.Context.Response.StatusCode;
    }

    // Builds a routable probe path: each parameter is filled with a value that satisfies its route constraint (an int
    // constraint → "1", otherwise a GUID) so routing matches the endpoint and the authorization layer is what answers.
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
