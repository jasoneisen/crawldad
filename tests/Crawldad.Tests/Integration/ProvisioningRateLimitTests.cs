using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The provisioning rate limit end-to-end (issue #119 PR7): on a host with a tiny per-account limit and an advanceable
/// clock, a second provision attempt in the window is a <c>429</c> BEFORE the handler runs (nothing created), and one window
/// later the account reaches the handler again — which now refuses with the one-per-email <c>409</c>, proving both that the
/// limiter gates the pre-membership provisioning surface and that its window recovers.</summary>
[Collection(ProvisioningRateLimitCollection.Name)]
public sealed class ProvisioningRateLimitTests(ProvisioningRateLimitFixture fixture) : IAsyncLifetime
{
    private const string _email = "rl-prov@crawldad.test";

    public Task InitializeAsync() => fixture.Host.ResetAllMartenDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<IScenarioResult> ProvisionAsync() =>
        fixture.Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, _email);
            x.Post.Json(new { }).ToUrl("/provisioning/tenants");
            x.IgnoreStatusCode();
        });

    [Fact]
    public async Task Provision_attempts_are_rate_limited_and_the_window_recovers()
    {
        (await ProvisionAsync()).Context.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);            // 1st admitted (PermitLimit = 1)
        (await ProvisionAsync()).Context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);   // 2nd over the sliding limit → refused pre-handler

        fixture.Clock.Advance(TimeSpan.FromSeconds(60));                                                        // the window slides past the first attempt

        // Recovered: the request reaches the handler again — which now refuses with the one-per-email 409 (not a 429).
        (await ProvisionAsync()).Context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }
}

/// <summary>A console-enabled host with a tiny provisioning rate limit (PermitLimit = 1) driven off an advanceable clock. Its
/// own schema keeps it isolated from the other console hosts.</summary>
public sealed class ProvisioningRateLimitFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public AdvanceableClock Clock { get; } = new(DateTimeOffset.UnixEpoch);

    public async Task InitializeAsync()
    {
        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_iso_pr7_rl");
            builder.UseSetting($"{ConsoleAuthOptions.Section}:TenantId", ConsoleAuthTestHarness.TenantId);
            builder.UseSetting($"{ConsoleAuthOptions.Section}:Audience", ConsoleAuthTestHarness.Audience);
            builder.UseSetting($"{ConsoleWriteOptions.Section}:PermitLimit", "1");
            builder.UseSetting($"{ConsoleWriteOptions.Section}:WindowSeconds", "60");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.Configure<JwtBearerOptions>(ConsoleAuthModule.Scheme, ConsoleAuthTestHarness.InjectTestKey);
            });
        });

        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>The xUnit collection for the tiny-limit provisioning host (its own schema, isolated from the other console hosts).</summary>
[CollectionDefinition(Name)]
public sealed class ProvisioningRateLimitCollection : ICollectionFixture<ProvisioningRateLimitFixture>
{
    public const string Name = "provisioning-rate-limit";
}
