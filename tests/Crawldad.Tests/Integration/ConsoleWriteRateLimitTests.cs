using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The console-write rate limit end-to-end (issue #119 PR5): on a host configured with a tiny per-<c>(email,
/// tenant)</c> limit and driven off an advanceable clock, a console write over the sliding limit is a <c>429</c> before the
/// handler runs, and the partition recovers one window later. Proves the guard is wired into the live pipeline (the middleware
/// branches are unit-covered separately).</summary>
[Collection(ConsoleWriteLimitCollection.Name)]
public sealed class ConsoleWriteRateLimitTests(ConsoleWriteLimitFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private const string _email = "rl@crawldad.test";
    private const string _tenantId = "3a3a3a3a-1111-4a2b-9c3d-0123456789ab";
    private const string _payload = """{ "crawldad": "1", "name": "demo", "config": { "backend": "input.backend" }, "steps": [], "result": "'v1'" }""";

    public async Task InitializeAsync()
    {
        await fixture.Host.ResetAllMartenDataAsync();
        await fixture.Host.Services.GetRequiredService<ITenantRegistryStore>().CreateAsync(new RegistryTenant
        {
            Id = _tenantId,
            DisplayName = "Rate Limited",
            Actor = _tenantId,
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, _ct);
        await fixture.Host.Services.GetRequiredService<ITenantMembershipStore>().CreateOwnerAsync(_tenantId, _email, DateTimeOffset.UnixEpoch, _ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> ConsoleDraftAsync()
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, _email);
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, _tenantId);
            x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(_payload) }).ToUrl("/payloads");
            x.IgnoreStatusCode();
        });
        return result.Context.Response.StatusCode;
    }

    [Fact]
    public async Task Console_writes_over_the_limit_are_429_and_recover_after_the_window()
    {
        (await ConsoleDraftAsync()).ShouldBe(StatusCodes.Status200OK);            // 1st admitted (PermitLimit = 1)
        (await ConsoleDraftAsync()).ShouldBe(StatusCodes.Status429TooManyRequests); // 2nd over the sliding limit

        fixture.Clock.Advance(TimeSpan.FromSeconds(60));                          // the window slides past the first write

        (await ConsoleDraftAsync()).ShouldBe(StatusCodes.Status200OK);           // recovered
    }
}

/// <summary>A console-enabled host with a tiny console-write limit (PermitLimit = 1) driven off an advanceable clock, so the
/// sliding window can be moved deterministically. Its own schema keeps it isolated from the default console host.</summary>
public sealed class ConsoleWriteLimitFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public AdvanceableClock Clock { get; } = new(DateTimeOffset.UnixEpoch);

    public async Task InitializeAsync()
    {
        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_iso_pr5_ratelimit");
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

/// <summary>The xUnit collection for the tiny-limit console-write host (its own schema, isolated from the default host).</summary>
[CollectionDefinition(Name)]
public sealed class ConsoleWriteLimitCollection : ICollectionFixture<ConsoleWriteLimitFixture>
{
    public const string Name = "console-write-limit";
}
