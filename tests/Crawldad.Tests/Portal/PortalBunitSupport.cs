using Crawldad.Portal.Auth;
using Crawldad.Portal.Tenancy;
using Microsoft.AspNetCore.Components.Forms;

namespace Crawldad.Tests.Portal;

/// <summary>A no-op <see cref="IPortalWorkspaceSelectionStore"/> for rendering the account/shell components under bUnit
/// (issue #119 PR6): the active-workspace preference is never set in these isolated renders, so reads return null and the
/// resolution falls back to the link tenant. The switch/attach behaviour is covered by the endpoint tests + the integration
/// test.</summary>
internal sealed class StubWorkspaceSelectionStore : IPortalWorkspaceSelectionStore
{
    public Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult<PortalWorkspaceSelection?>(null);

    public Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A programmable <see cref="IPortalAuthService"/> for rendering the login component in isolation.</summary>
internal sealed class FakePortalAuthService : IPortalAuthService
{
    public RequestCodeOutcome RequestOutcome { get; set; } = RequestCodeOutcome.Sent;

    public VerifyResult VerifyResultToReturn { get; set; } =
        VerifyResult.Fail(VerifyOutcome.InvalidCode, "user@example.com");

    public List<string> RequestedEmails { get; } = [];

    public Task<RequestCodeOutcome> RequestCodeAsync(string email, CancellationToken cancellationToken)
    {
        RequestedEmails.Add(email);
        return Task.FromResult(RequestOutcome);
    }

    public Task<VerifyResult> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken) =>
        Task.FromResult(VerifyResultToReturn);
}

/// <summary>Satisfies the <c>&lt;AntiforgeryToken /&gt;</c> / EditForm antiforgery lookup when rendering SSR forms
/// under bUnit (there is no real request pipeline to supply one).</summary>
internal sealed class StubAntiforgeryStateProvider : AntiforgeryStateProvider
{
    public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("stub-token", "__RequestVerificationToken");
}
