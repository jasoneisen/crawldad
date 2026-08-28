using System.Diagnostics.CodeAnalysis;
using Crawldad.Client;
using Crawldad.Portal.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Portal.Tenancy;

/// <summary>Builds a console-mode <see cref="CrawldadClient"/> for a request (issue #119 PR4/PR5): a client whose credential
/// is the portal's first-party <see cref="ConsoleCredential"/> (bearer token from the managed identity + the user/workspace
/// selectors). While a transition stored key remains it rides the shared API handler wrapped by a
/// <see cref="ConsoleReadFallbackHandler"/> so a rejected console <b>read</b> retries once with the key; writes stay
/// console-only. Once the key is retired (a keyless console link) it rides the handler directly — pure console, no fallback.
/// Registered only when console-mode is configured; its absence is what keeps <see cref="PortalTenantContext"/> on the
/// byte-identical stored-key path.</summary>
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

    /// <summary>Builds a console client acting as <paramref name="consoleUser"/> in <paramref name="workspace"/>. When
    /// <paramref name="fallbackApiKey"/> is non-empty a console-<b>read</b> rejection retries once with that stored key (the
    /// transition read-fallback); when null/empty the client is pure console with no fallback. Writes are console-only either
    /// way.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The HttpClient wraps a POOLED handler (disposeHandler:false) and is owned by the returned CrawldadClient for the whole request scope — disposing it here would break every call the client then makes; the pooled inner handler is owned by IHttpMessageHandlerFactory, not this client. The optional ConsoleReadFallbackHandler is a DelegatingHandler over that pooled handler and is likewise not disposed.")]
    public CrawldadClient Build(string consoleUser, string workspace, string? fallbackApiKey)
    {
        var inner = _handlerFactory.CreateHandler(PortalTenancy.ApiHttpClientName);
        HttpMessageHandler outer = string.IsNullOrEmpty(fallbackApiKey)
            ? inner                                                    // keyless console link — pure console, no fallback
            : new ConsoleReadFallbackHandler(inner, fallbackApiKey);   // transition read-fallback to the stored key
        var http = new HttpClient(outer, disposeHandler: false) { BaseAddress = _apiBaseUrl };
        var credential = new ConsoleCredential(_tokenSource.GetTokenAsync, consoleUser, workspace);
        return new CrawldadClient(http, new CrawldadClientOptions { Credential = credential });
    }
}
