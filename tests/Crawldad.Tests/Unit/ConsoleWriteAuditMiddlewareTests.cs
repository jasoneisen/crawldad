using System.Security.Claims;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The console-write guard middleware (issue #119 PR5) in isolation: it acts only on a console-authenticated write
/// to an enumerated route — rate-limiting it and appending an audit row — and passes a read, an <c>ApiKey</c> write, and an
/// unmatched request straight through. Audit is best-effort, so a store fault never fails the write.</summary>
public class ConsoleWriteAuditMiddlewareTests
{
    private const string _route = "/payloads/{id}/revise";
    private const string _tenant = "t-1";
    private const string _email = "owner@x.test";

    private static DefaultHttpContext Context(bool consoleAuthed = true, string method = "POST", string? route = _route)
    {
        var context = new DefaultHttpContext
        {
            // ProblemHttpResult.ExecuteAsync resolves ILoggerFactory from request services (the real app registers it).
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (route is not null)
        {
            context.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask, RoutePatternFactory.Parse(route), 0, new EndpointMetadataCollection(), route));
        }

        var authType = consoleAuthed ? ConsoleAuthModule.Scheme : CrawldadAuthentication.Scheme;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(CrawldadClaims.TenantId, _tenant), new Claim(CrawldadClaims.Actor, _email)], authType));
        return context;
    }

    private static ConsoleWriteRateLimiter Limiter(int permitLimit) =>
        new(Options.Create(new ConsoleWriteOptions { PermitLimit = permitLimit, WindowSeconds = 60 }), new FakeClock());

    private static ConsoleWriteAuditMiddleware Middleware(RequestDelegate next, ConsoleWriteRateLimiter limiter, IConsoleAuditStore audit) =>
        new(next, limiter, audit, new FakeClock(), NullLogger<ConsoleWriteAuditMiddleware>.Instance);

    [Fact]
    public async Task A_console_write_under_the_limit_runs_the_handler_and_audits_the_outcome()
    {
        var audit = new RecordingAuditStore();
        var nextCalled = false;
        var context = Context();

        await Middleware(ctx => { nextCalled = true; ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Limiter(100), audit)
            .InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.TenantId.ShouldBe(_tenant);
        entry.Email.ShouldBe(_email);      // the human actor, not the shared portal identity
        entry.Operation.ShouldBe("POST");
        entry.Route.ShouldBe(_route);      // the template, never a concrete id
        entry.StatusCode.ShouldBe(200);    // the handler's outcome
    }

    [Fact]
    public async Task A_console_write_over_the_limit_is_429_and_the_handler_never_runs_and_nothing_is_audited()
    {
        var audit = new RecordingAuditStore();
        var nextCalled = false;
        var context = Context();

        await Middleware(_ => { nextCalled = true; return Task.CompletedTask; }, Limiter(0), audit).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        nextCalled.ShouldBeFalse();      // rejected before the handler
        audit.Entries.ShouldBeEmpty();   // no audit row for a throttled attempt (the limiter bounds audit volume)
    }

    [Fact]
    public async Task An_audit_store_fault_does_not_fail_the_write()
    {
        var nextCalled = false;
        var context = Context();

        await Should.NotThrowAsync(
            Middleware(ctx => { nextCalled = true; ctx.Response.StatusCode = 201; return Task.CompletedTask; }, Limiter(100), new ThrowingAuditStore())
                .InvokeAsync(context));

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(201); // the write's own result stands despite the audit fault
    }

    [Fact]
    public async Task A_handler_that_throws_propagates_and_is_not_audited_and_restores_the_response_body()
    {
        var audit = new RecordingAuditStore();
        var context = Context();
        var originalBody = context.Response.Body;

        await Should.ThrowAsync<InvalidOperationException>(
            Middleware(_ => throw new InvalidOperationException("boom"), Limiter(100), audit).InvokeAsync(context));

        audit.Entries.ShouldBeEmpty();                      // a failed write is never audited (the buffer is discarded)
        context.Response.Body.ShouldBeSameAs(originalBody); // the original body is restored for the exception handler
    }

    [Fact]
    public async Task An_api_key_write_to_a_console_write_route_is_not_audited_or_limited()
    {
        var audit = new RecordingAuditStore();
        var context = Context(consoleAuthed: false); // an ApiKey identity, not the console scheme

        await Middleware(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Limiter(0), audit).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(200); // NOT 429 — the guard ignores the key path even at limit 0
        audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_console_read_is_not_audited_or_limited()
    {
        var audit = new RecordingAuditStore();
        var context = Context(method: "GET", route: "/tenant"); // a console-authed read, not a write route

        await Middleware(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Limiter(0), audit).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(200);
        audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_with_no_matched_endpoint_passes_through()
    {
        var audit = new RecordingAuditStore();
        var nextCalled = false;
        var context = Context(route: null); // no endpoint (a 404-no-route)

        await Middleware(_ => { nextCalled = true; return Task.CompletedTask; }, Limiter(0), audit).InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        audit.Entries.ShouldBeEmpty();
    }

    private sealed class RecordingAuditStore : IConsoleAuditStore
    {
        public List<ConsoleAuditEntry> Entries { get; } = [];

        public Task RecordAsync(ConsoleAuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConsoleAuditEntry>> ListForTenantAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConsoleAuditEntry>>(Entries);
    }

    private sealed class ThrowingAuditStore : IConsoleAuditStore
    {
        public Task RecordAsync(ConsoleAuditEntry entry, CancellationToken ct) =>
            throw new InvalidOperationException("audit store down");

        public Task<IReadOnlyList<ConsoleAuditEntry>> ListForTenantAsync(string tenantId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
