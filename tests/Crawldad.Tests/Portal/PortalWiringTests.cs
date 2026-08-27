using Alba;
using Crawldad.Portal.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>Boots the portal host per environment to cover the environment-conditional wiring: Development
/// registers the logging sender (and applies the schema + relaxes the cookie policy), while every other
/// environment fails closed and adds the production error handler + HSTS. Shares the portal collection so it
/// never races the shared host on Postgres.</summary>
[Collection(PortalCollection.Name)]
public class PortalWiringTests
{
    [Fact]
    public async Task Development_registers_the_logging_email_sender()
    {
        await using var host = await AlbaHost.For<Crawldad.Portal.Program>(b => b.UseEnvironment("Development"));

        host.Services.GetRequiredService<IEmailSender>().ShouldBeOfType<LoggingEmailSender>();
    }

    [Fact]
    public async Task Production_fails_closed_and_still_serves_the_public_home()
    {
        await using var host = await AlbaHost.For<Crawldad.Portal.Program>(b => b.UseEnvironment("Production"));

        // Fail-closed sender: no codes leak until a real provider is wired.
        host.Services.GetRequiredService<IEmailSender>().ShouldBeOfType<UnconfiguredEmailSender>();

        // The production pipeline (exception handler + HSTS) still serves anonymous marketing content.
        await host.Scenario(x =>
        {
            x.Get.Url("/");
            x.StatusCodeShouldBeOk();
        });
    }
}
