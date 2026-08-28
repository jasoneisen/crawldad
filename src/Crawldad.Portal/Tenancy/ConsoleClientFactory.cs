using System.Diagnostics.CodeAnalysis;
using Crawldad.Client;
using Crawldad.Portal.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Portal.Tenancy;

/// <summary>Builds a console-mode <see cref="CrawldadClient"/> for a request (issue #119 PR4): a client whose credential is
/// the portal's first-party <see cref="ConsoleCredential"/> (bearer token from the managed identity + the user/workspace
/// selectors), riding on the shared API handler wrapped by a <see cref="ConsoleReadFallbackHandler"/> so a rejected console
/// request transparently retries with the tenant's stored key. Registered only when console-mode is configured; its absence
/// is what keeps <see cref="PortalTenantContext"/> on the byte-identical stored-key path.</summary>
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
        _apiBaseUrl = PortalTenancy.ResolveApiBaseUrl(configuration); // the same boot-validated base URL the key client uses
    }

    /// <summary>Builds a console client acting as <paramref name="consoleUser"/> in <paramref name="workspace"/>, falling
    /// back to <paramref name="fallbackApiKey"/> on a console-auth rejection.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The HttpClient wraps a POOLED handler (disposeHandler:false) and is owned by the returned CrawldadClient for the whole request scope — disposing it here would break every call the client then makes; the pooled inner handler is owned by IHttpMessageHandlerFactory, not this client.")]
    public CrawldadClient Build(string consoleUser, string workspace, string fallbackApiKey)
    {
        var inner = _handlerFactory.CreateHandler(PortalTenancy.ApiHttpClientName);
        var http = new HttpClient(new ConsoleReadFallbackHandler(inner, fallbackApiKey), disposeHandler: false) { BaseAddress = _apiBaseUrl };
        var credential = new ConsoleCredential(_tokenSource.GetTokenAsync, consoleUser, workspace);
        return new CrawldadClient(http, new CrawldadClientOptions { Credential = credential });
    }
}
