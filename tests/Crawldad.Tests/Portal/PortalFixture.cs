using Crawldad.Portal;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Support;
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

/// <summary>A hand-cranked <see cref="TimeProvider"/> so tests can drive code expiry deterministically. "Now" only ever
/// moves FORWARD — see <see cref="PortalTestHost.Clock"/> for why rewinding (or starting in the past) is not safe here.</summary>
public sealed class ControllableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves "now" forward by <paramref name="by"/>. Rejects a negative step: rewinding would push the auth
    /// cookie's <c>Expires</c> back toward (or past) real time — the failure <see cref="PortalTestHost.Clock"/> describes.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        _now = _now.Add(by);
    }
}

/// <summary>The portal's test-host wiring, shared by <see cref="PortalTestHost"/> and by the environment-conditional
/// wiring tests that boot the portal through Alba — so every portal host in the suite lands on the same TEST schema.</summary>
internal static class PortalTesting
{
    /// <summary>The portal suite's Marten schema. The portal ships on the production-named <c>portal</c> schema; on the
    /// shared developer Postgres that is the API's "run against the real schema" problem in miniature — the suite's rows
    /// and a developer's local portal data land in the same tables. A test-owned name keeps them apart AND makes the
    /// schema droppable (<see cref="TestSchema.EnsureDroppable"/> refuses <c>portal</c> outright).</summary>
    public const string SchemaName = "crawldad_portal_test";

    /// <summary>Points a portal host at <see cref="SchemaName"/>, dropping it first when <paramref name="resetData"/> —
    /// the same drop-before-boot the API hosts get (<c>TestDefaults.UseCrawldadTestDefaults</c>). Only the collection
    /// fixture's host resets: the extra hosts the wiring tests build share the live fixture's schema, so a drop there
    /// would pull the tables out from under the rest of the collection.</summary>
    public static IWebHostBuilder UsePortalTestSchema(this IWebHostBuilder builder, bool resetData = false)
    {
        if (resetData)
        {
            TestSchema.Drop(SchemaName, typeof(Crawldad.Portal.Program));
        }

        // ConfigureTestServices runs after the app's own AddMarten, so this IConfigureMarten wins — the same seam the API
        // suite uses to isolate each fixture on its own schema.
        return builder.ConfigureTestServices(services => services.ConfigureMarten(options => options.DatabaseSchemaName = SchemaName));
    }
}

/// <summary>The portal host under test. Boots the real <see cref="PortalHost"/> wiring in the given environment,
/// then swaps in the capturing email sender and controllable clock (via ConfigureTestServices, which runs after
/// the app's own registrations and therefore wins).</summary>
public sealed class PortalTestHost(string environment) : WebApplicationFactory<Crawldad.Portal.Program>
{
    public CapturingEmailSender Email { get; } = new();

    /// <summary>The host's clock. It starts at the REAL "now" and must NEVER be pinned to a hard-coded instant. The portal
    /// signs in with <c>AuthenticationProperties.IsPersistent</c>, and ASP.NET Core's cookie handler resolves its clock from
    /// the DI <see cref="TimeProvider"/> — this one — so <c>Set-Cookie</c> carries <c>expires = clock-now + ExpireTimeSpan</c>
    /// (7 days, PortalHost.AddCookieAuthentication). The test client's <see cref="System.Net.CookieContainer"/> judges that
    /// expiry against the REAL clock and silently DROPS an already-expired cookie, so a fixed start instant is a dormant time
    /// bomb: every authenticated portal test turns red — unauthenticated, 302 to /login — the day real time passes it by more
    /// than ExpireTimeSpan, with nothing in the repo having changed. Tests only ever use this clock RELATIVELY (Advance, and
    /// comparisons against GetUtcNow()), so tracking real time costs no determinism and cannot rot.</summary>
    public ControllableTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UsePortalTestSchema(resetData: true); // the collection's one shared host: start from a provably empty schema
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
        // Force the host to build + start (the drop-before-boot runs here, and the Development boot then applies the test
        // schema), then belt-and-suspenders apply it explicitly so a query in any test finds its tables.
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
