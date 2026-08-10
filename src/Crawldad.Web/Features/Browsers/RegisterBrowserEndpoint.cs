using Crawldad.Contracts.Browsers;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Browsers;

/// <summary><c>PUT /browsers/{name}</c>: register or replace a browser connect credential for the authenticated tenant.
/// The body (adapter/mode/secret/options) is validated by <see cref="RegisterBrowserRequestValidator"/>; the name slug
/// is guarded here. The secret is encrypted at rest and never echoed — the response is the stored metadata only.</summary>
public static class RegisterBrowserEndpoint
{
    [WolverinePut("/browsers/{name}")]
    public static async Task<IResult> Handle(
        string name,
        RegisterBrowserRequest request,
        IBrowserCredentialStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        if (!BrowserRegistrationRules.IsValidName(name))
        {
            return BrowserProblems.InvalidName();
        }

        var summary = await store.RegisterAsync(
            tenant.TenantId, name, request.Adapter, request.Mode, request.Secret, request.Options, ct);
        return Results.Ok(summary);
    }
}
