using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Docs;

/// <summary><c>GET /openapi.json</c>: serves the generated OpenAPI 3.1 description of the HTTP envelope — every routable
/// endpoint, its authentication, the request/response contracts, and status codes. Built once in <see cref="OpenApiSpec"/>.
/// Deliberately anonymous: it documents the auth requirements, so it need not require auth itself.</summary>
public static class OpenApiEndpoint
{
    [AllowAnonymous]
    [WolverineGet("/openapi.json")]
    public static IResult Get() => Results.Text(OpenApiSpec.DocumentJson, "application/json", Encoding.UTF8);
}
