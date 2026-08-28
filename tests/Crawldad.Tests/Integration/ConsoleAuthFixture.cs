using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>A host with the <c>ConsolePrincipal</c> scheme ENABLED (issue #119 PR4): <c>Crawldad:ConsoleAuth</c> points at
/// the <see cref="ConsoleAuthTestHarness"/>'s test issuer/audience, and the test-only signing-key configurator is layered
/// after the app's own JwtBearer config (post-Program <c>ConfigureServices</c> wins), so the REAL validator runs against
/// test-issued tokens. This is the fixture the console-read end-to-end tests + the enumeration test drive. No default auth
/// header is set — each scenario presents exactly the credential it is testing (a console bearer + selectors, or an API key).</summary>
public sealed class ConsoleAuthFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_iso_pr4_console");
            builder.UseSetting($"{ConsoleAuthOptions.Section}:TenantId", ConsoleAuthTestHarness.TenantId);
            builder.UseSetting($"{ConsoleAuthOptions.Section}:Audience", ConsoleAuthTestHarness.Audience);

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeClock());
                // Swap the signing-key SOURCE only (static test key instead of Entra JWKS); issuer/audience/role/version are
                // validated exactly as in production. Runs after the module's own config, so it wins.
                services.Configure<JwtBearerOptions>(ConsoleAuthModule.Scheme, ConsoleAuthTestHarness.InjectTestKey);
            });
        });

        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>The xUnit collection for the console-auth-enabled host (its own schema, isolated from the default host).</summary>
[CollectionDefinition(Name)]
public sealed class ConsoleAuthCollection : ICollectionFixture<ConsoleAuthFixture>
{
    public const string Name = "console-auth";
}
