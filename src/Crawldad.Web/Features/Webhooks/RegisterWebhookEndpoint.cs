using Crawldad.Contracts.Webhooks;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Webhooks;

/// <summary><c>PUT /webhooks/{name}</c>: register or replace a webhook endpoint for the authenticated tenant. The body
/// (url/secret/events) is validated by <see cref="RegisterWebhookRequestValidator"/> (https + non-private target, secret
/// length, known event types); the name slug is guarded here. The signing secret is encrypted at rest and never echoed —
/// the response is the stored metadata only.</summary>
public static class RegisterWebhookEndpoint
{
    /// <summary>Handles <c>PUT /webhooks/{name}</c>.</summary>
    [WolverinePut("/webhooks/{name}")]
    public static async Task<IResult> Handle(
        string name,
        RegisterWebhookRequest request,
        IWebhookEndpointStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        if (!WebhookRegistrationRules.IsValidName(name))
        {
            return WebhookProblems.InvalidName();
        }

        var summary = await store.RegisterAsync(tenant.TenantId, name, request.Url, request.Secret, request.Events ?? [], ct);
        return Results.Ok(summary);
    }
}
