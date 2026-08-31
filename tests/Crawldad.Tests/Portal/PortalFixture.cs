using System.Globalization;
using Crawldad.Portal;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crawldad.Tests.Portal;

/// <summary>The dev/CI console token source: a static fake token so the portal runs in console-mode without any Azure
/// managed identity — exactly the "console-mode config with a test/fake token source" dev-loop the portal ships with
/// (issue #119). Console API calls the tests don't stub simply fail against the unreachable API base URL and degrade to the
/// pages' empty/error states; the states that matter (no active workspace, resolution) need no live API.</summary>
internal sealed class FakeConsoleTokenSource : IConsoleTokenSource
{
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult("fake-console-token");
}

/// <summary>Captures the OTP codes the auth service "sends" so a test can complete the flow, and records every
/// send for assertions.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly Lock _gate = new();
    private readonly List<(string Email, string Code)> _sent = [];

    public IReadOnlyList<(string Email, string Code)> Sent
    {
        get { lock (_gate) { return [.. _sent]; } }
    }

    /// <summary>The most recent code captured for <paramref name="email"/> (normalized).</summary>
    public string LastCodeFor(string email)
    {
        lock (_gate)
        {
            for (var i = _sent.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_sent[i].Email, email, StringComparison.Ordinal))
                {
                    return _sent[i].Code;
                }
            }
        }

        throw new InvalidOperationException($"No OTP code was captured for '{email}'.");
    }

    public Task SendOtpCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        lock (_gate) { _sent.Add((email, code)); }
        return Task.CompletedTask;
    }
}

/// <summary>A hand-cranked <see cref="TimeProvider"/> so tests can drive code expiry deterministically.</summary>
public sealed class ControllableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>The portal host under test. Boots the real <see cref="PortalHost"/> wiring in the given environment,
/// then swaps in the capturing email sender and controllable clock (via ConfigureTestServices, which runs after
/// the app's own registrations and therefore wins).</summary>
public sealed class PortalTestHost(string environment) : WebApplicationFactory<Crawldad.Portal.Program>
{
    public CapturingEmailSender Email { get; } = new();

    public ControllableTimeProvider Clock { get; } =
        new(DateTimeOffset.Parse("2026-08-27T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // Run the portal in CONSOLE-MODE (issue #119): the portal is console-mode only for data, so the tests exercise the
            // real dev/prod path. AddConsoleAuth is config-gated and reads config at build time (which WebApplicationFactory's
            // config additions don't reach), so force the console wiring here: a dev/CI FAKE token source (no Azure identity)
            // plus the ConsoleClientFactory the resolvers inject as their optional dependency — exactly the "console-mode with
            // a test/fake token source" dev-loop. An unlinked user (no active-workspace selection) resolves to a clean
            // "No workspace yet" with no live API call; console API calls the tests don't stub degrade to the empty/error states.
            services.RemoveAll<IConsoleTokenSource>();
            services.AddSingleton<IConsoleTokenSource>(new FakeConsoleTokenSource());
            services.AddSingleton<ConsoleClientFactory>();
        });
    }

    public IDocumentStore Store => Services.GetRequiredService<IDocumentStore>();
}

/// <summary>One shared Development host for the whole portal collection — mirrors the API suite's reuse of a
/// single host, so concurrent Marten schema migrations never race on the shared Postgres. Tests isolate
/// themselves with unique emails; the clock only ever moves forward.</summary>
public sealed class PortalFixture : IAsyncLifetime
{
    public PortalTestHost App { get; } = new("Development");

    public async Task InitializeAsync()
    {
        // Force the host to build + start (Development boot applies the "portal" schema), then belt-and-suspenders
        // apply it explicitly so a query in any test finds its tables.
        using var _ = App.CreateClient();
        await App.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();

    /// <summary>A fresh client with its own cookie jar and no auto-redirect — the shape a cookie/redirect flow
    /// needs.</summary>
    public HttpClient NewClient() =>
        App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
}

[CollectionDefinition(PortalCollection.Name)]
public sealed class PortalCollection : ICollectionFixture<PortalFixture>
{
    public const string Name = "portal";
}
