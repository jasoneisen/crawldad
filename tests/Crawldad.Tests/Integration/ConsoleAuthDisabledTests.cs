using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>With no <c>Crawldad:ConsoleAuth</c> configured (the default host, e.g. <see cref="AppFixture"/>), the
/// console-principal scheme is never registered: <c>ApiKey</c> stays the sole/default authentication scheme and the host
/// is byte-for-byte unchanged. This is the "inert until configured" guarantee — the same reason PR2 changes zero runtime
/// behaviour (issue #119 PR2), asserted against the live composed host, not a hand-built container.</summary>
[Collection(IntegrationCollection.Name)]
public class ConsoleAuthDisabledTests(AppFixture fixture)
{
    [Fact]
    public async Task Console_scheme_is_unregistered_and_apikey_stays_the_only_default()
    {
        var schemes = fixture.Host.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        // The console scheme is absent — no endpoint could ever select it on this host.
        (await schemes.GetSchemeAsync(ConsoleAuthModule.Scheme)).ShouldBeNull();

        // ApiKey remains the sole default scheme (finding #4 — ConsolePrincipal never becomes a default/fallback).
        var defaultAuthenticate = await schemes.GetDefaultAuthenticateSchemeAsync();
        defaultAuthenticate!.Name.ShouldBe(CrawldadAuthentication.Scheme);

        var registered = (await schemes.GetAllSchemesAsync()).Select(s => s.Name).ToList();
        registered.ShouldContain(CrawldadAuthentication.Scheme);
        registered.ShouldNotContain(ConsoleAuthModule.Scheme);
    }
}
