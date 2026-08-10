using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Docs;

/// <summary>
/// <c>GET /openapi.json</c> (#21): serves the generated OpenAPI 3.1 description of the HTTP envelope — every routable
/// endpoint, its authentication, the request/response contracts from <c>Crawldad.Contracts</c>, and the status codes
/// (including the <c>202</c> running/queued run shapes and the <c>429 queue_depth_exceeded</c> admission limit). The document
/// is built once in <see cref="OpenApiSpec"/> and served verbatim as <c>application/json</c>.
/// <para>
/// <b>Deliberately anonymous (CD-1),</b> the same opt-out as <c>/health</c>, the payload schema, and <c>/llms.txt</c>: an
/// OpenAPI description of the public HTTP surface carries no tenant data and is only useful as a discovery/reference artifact
/// when reachable without a key. It documents the auth requirements; it need not require auth itself. Allowlisted in the
/// endpoint-enumeration auth test alongside the other anonymous docs routes.
/// </para>
/// </summary>
public static class OpenApiEndpoint
{
    /// <summary>Handles <c>GET /openapi.json</c>.</summary>
    /// <returns><c>200</c> with the generated OpenAPI 3.1 document as <c>application/json</c>.</returns>
    [AllowAnonymous]
    [WolverineGet("/openapi.json")]
    public static IResult Get() => Results.Text(OpenApiSpec.DocumentJson, "application/json", Encoding.UTF8);
}
