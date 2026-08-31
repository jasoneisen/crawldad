using Crawldad.Portal.Tenancy;

namespace Crawldad.Portal.Auth;

/// <summary>Decides where a freshly OTP-verified <c>/signup</c> account lands (issue #119 PR8). Signup is the <b>same</b>
/// passwordless email-OTP mechanic as <c>/login</c>; the post-verification landing is the <b>only</b> thing signup does
/// differently — it turns a brand-new, zero-workspace account into a provisioned one and sends it to a first-run dashboard,
/// while a returning account (one that already has a linked workspace) is treated exactly like a <c>/login</c> sign-in.
/// Kept out of the page so each arm is unit-testable without a cookie pipeline, and so the enumeration-safe request→verify
/// steps stay byte-identical to the login page.</summary>
internal interface ISignupLanding
{
    /// <summary>Resolves the post-verification destination for <paramref name="verifiedEmail"/> (already normalized by the
    /// auth service, and proven controlled by the just-verified OTP). A <b>returning</b> account (already linked to a
    /// workspace) → the open-redirect-guarded <paramref name="returnUrl"/>, byte-identical to <c>/login</c>; a
    /// <b>zero-workspace</b> account → its one free-workspace provision, landing on the first-run dashboard on success, on the
    /// account page in stored-key mode (nothing to provision with — honest), or on the account page carrying a safe error on a
    /// transient failure.</summary>
    Task<string> ResolveAsync(string verifiedEmail, string? returnUrl, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISignupLanding"/>
internal sealed class SignupLanding : ISignupLanding
{
    /// <summary>The first-run dashboard: the just-provisioned workspace is active, and <c>?welcome=true</c> renders the on-ramp
    /// empty state (mint a key, declare a payload). The value is <c>true</c>, not <c>1</c>, because the dashboard binds it to a
    /// <see cref="bool"/> query parameter (which parses <c>true</c>/<c>false</c>, never <c>1</c>).</summary>
    internal const string FirstRunDashboard = "/app?welcome=true";

    /// <summary>Where an unconfigured-console signup (no console identity to provision with) or a failed provision lands — the
    /// account page, whose zero-workspace state explains the reality and offers the claim form. Matches
    /// <see cref="WorkspaceProvisionEndpoints.AccountPath"/> so the two provisioning entry points land consistently.</summary>
    internal const string AccountPath = "/app/account";

    private readonly IPortalWorkspaceSelectionStore _selections;
    private readonly IPortalProvisioningService _provisioning;

    public SignupLanding(IPortalWorkspaceSelectionStore selections, IPortalProvisioningService provisioning)
    {
        _selections = selections;
        _provisioning = provisioning;
    }

    public async Task<string> ResolveAsync(string verifiedEmail, string? returnUrl, CancellationToken cancellationToken)
    {
        // A returning account (one that already has an active workspace) is a /login in disguise: never re-provision, and
        // honour the return URL through the same same-site open-redirect guard the login page uses. Only the zero-workspace
        // population — exactly who signup is for — is provisioned. (An account that has a workspace at the API but lost its
        // active-workspace pointer is still handled: it has no selection here, so it flows to ProvisionAsync, whose
        // one-per-email 409 recovers the existing workspace and re-selects it.)
        if (await _selections.GetAsync(verifiedEmail, cancellationToken) is not null)
        {
            return SafeRedirect.ToLocalOrApp(returnUrl);
        }

        var result = await _provisioning.ProvisionAsync(verifiedEmail, displayName: null, cancellationToken);
        return result.Outcome switch
        {
            // Created, or one-per-email-ever recovered — either way the account now has its free workspace active.
            PortalProvisionOutcome.Provisioned or PortalProvisionOutcome.AlreadyProvisioned => FirstRunDashboard,
            // Console access unconfigured on this deployment: there is no console identity to create a workspace with. Honest —
            // the account page's zero-workspace state explains it and offers the "claim an existing workspace" action.
            PortalProvisionOutcome.Unavailable => AccountPath,
            // A transient provision failure (API unreachable / rate-limited): surface the safe message on the account page,
            // where the one-click "create your free workspace" affordance lets the user retry.
            _ => $"{AccountPath}?provisionError={Uri.EscapeDataString(result.Message)}",
        };
    }
}
