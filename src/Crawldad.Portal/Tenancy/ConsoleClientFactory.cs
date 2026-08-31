using System.Diagnostics.CodeAnalysis;
using Crawldad.Client;
using Crawldad.Portal.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Portal.Tenancy;

/// <summary>Builds a console-mode <see cref="CrawldadClient"/> for a request (issue #119): a client whose credential is the
/// portal's first-party <see cref="ConsoleCredential"/> (bearer token from the managed identity + the user/workspace
/// selectors). The portal is console-mode only — there is no stored tenant key and no read-fallback: the client rides the
/// shared pooled API handler directly. Registered only when console-mode is configured; its absence is what puts
/// <see cref="PortalTenantContext"/> into its honest "console access not configured" state.</summary>
internal sealed class ConsoleClientFactory
{
    private readonly IHttpMessageHandlerFactory _handlerFactory;
    private readonly IConsoleTokenSource _tokenSource;
    private readonly Uri _apiBaseUrl;

    public ConsoleClientFactory(IHttpMessageHandlerFactory handlerFactory, IConsoleTokenSource tokenSource, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _handlerFactory = handlerFactory;
        _tokenSource = tokenSource;
        _apiBaseUrl = PortalTenancy.ResolveApiBaseUrl(configuration); // the boot-validated API base URL
    }

    /// <summary>Builds a console client acting as <paramref name="consoleUser"/> in <paramref name="workspace"/> — pure
    /// console, no stored-key fallback.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The HttpClient wraps a POOLED handler (disposeHandler:false) and is owned by the returned CrawldadClient for the whole request scope — disposing it here would break every call the client then makes; the pooled inner handler is owned by IHttpMessageHandlerFactory, not this client.")]
    public CrawldadClient Build(string consoleUser, string workspace)
    {
        var inner = _handlerFactory.CreateHandler(PortalTenancy.ApiHttpClientName);
        var http = new HttpClient(inner, disposeHandler: false) { BaseAddress = _apiBaseUrl };
        var credential = new ConsoleCredential(_tokenSource.GetTokenAsync, consoleUser, workspace);
        return new CrawldadClient(http, new CrawldadClientOptions { Credential = credential });
    }

    /// <summary>Builds a console client for the pre-workspace provisioning call (issue #119 PR7): pure console (no fallback
    /// key — there is no workspace to fall back to) and a workspace-less credential (<see cref="ConsoleCredential.ForProvisioning"/>),
    /// so it authenticates as the portal acting for <paramref name="consoleUser"/> with no workspace selector. Valid only for
    /// <c>ProvisionTenantAsync</c>.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The HttpClient wraps a POOLED handler (disposeHandler:false) and is owned by the returned CrawldadClient for the whole call — disposing it here would break that call; the pooled inner handler is owned by IHttpMessageHandlerFactory, not this client.")]
    public CrawldadClient BuildForProvisioning(string consoleUser)
    {
        var inner = _handlerFactory.CreateHandler(PortalTenancy.ApiHttpClientName);
        var http = new HttpClient(inner, disposeHandler: false) { BaseAddress = _apiBaseUrl };
        var credential = ConsoleCredential.ForProvisioning(_tokenSource.GetTokenAsync, consoleUser);
        return new CrawldadClient(http, new CrawldadClientOptions { Credential = credential });
    }
}
