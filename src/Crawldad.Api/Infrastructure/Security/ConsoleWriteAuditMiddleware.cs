using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The console-write guard (issue #119 PR5): the one place a console-authenticated <b>write</b> is rate-limited and
/// audited. It runs after authentication/authorization, so it only ever sees a request that already passed the
/// <see cref="ConsoleAuthModule.ConsoleOrKeyPolicy"/> (a console token with no membership is a <c>403</c> upstream and never
/// reaches here). For every request it asks one question — is this a console-authenticated write to an enumerated
/// <see cref="ConsoleWriteEndpoints"/> route? — and only then acts:
///
/// <list type="bullet">
/// <item>a per-<c>(email, tenant)</c> sliding-window check (<see cref="ConsoleWriteRateLimiter"/>); over the limit is a
/// <c>429</c> <b>before</b> the handler runs (and no audit row, so the limiter bounds audit volume under abuse);</item>
/// <item>a single lightweight <see cref="ConsoleAuditEntry"/> after the handler — tenant, actor email, HTTP operation, route
/// template, response status, timestamp; no bodies, no secrets — appended best-effort so a store fault never fails the write.</item>
/// </list>
///
/// <para>A read, a programmatic <c>ApiKey</c> write (the same endpoint reached with a tenant key), and every non-console-write
/// route pass straight through untouched — attribution and abuse-insurance apply to the console channel alone.</para></summary>
internal sealed class ConsoleWriteAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConsoleWriteRateLimiter _limiter;
    private readonly IConsoleAuditStore _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConsoleWriteAuditMiddleware> _logger;

    public ConsoleWriteAuditMiddleware(
        RequestDelegate next,
        ConsoleWriteRateLimiter limiter,
        IConsoleAuditStore audit,
        TimeProvider clock,
        ILogger<ConsoleWriteAuditMiddleware> logger)
    {
        _next = next;
        _limiter = limiter;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Rate-limits + audits a console-authenticated write; passes everything else straight through.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var route = ConsoleWriteRoute(context);
        if (route is null)
        {
            await _next(context); // not a console-authenticated write — no rate limit, no audit
            return;
        }

        // The ConsoleOrKey policy required the tenant claim to reach here, and the console scheme stamps the actor (the human
        // email) alongside it — so both are present on a console write that passed authorization.
        var tenantId = context.User.FindFirstValue(CrawldadClaims.TenantId)!;
        var email = context.User.FindFirstValue(CrawldadClaims.Actor)!;

        if (!_limiter.TryAcquire(email, tenantId))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "console_write_rate_limited",
                detail: "too many console writes in a short window for this account and workspace; slow down and retry shortly")
                .ExecuteAsync(context);
            return; // refused before the handler — nothing mutated, no audit row
        }

        // Run the write with its response BUFFERED, so the audit row is committed before the response is flushed to the
        // client: the mutation and its audit land together (a caller who reads the audit back immediately always sees it),
        // and a failed handler leaves the buffer discarded and unaudited. Only console writes are buffered — every other
        // request passed straight through above, so streaming reads are untouched.
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context);
            await RecordAuditAsync(context, route, tenantId, email);
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody); // flush the real response only after the audit is durable
        }
        finally
        {
            context.Response.Body = originalBody; // always restore, even if the handler threw (the exception handler owns the response)
        }
    }

    // The route template when this request is a console-authenticated write to an enumerated route, else null. The verb comes
    // from the request itself (so no HttpMethodMetadata is required), and the console channel is proven by an identity the
    // ConsolePrincipal scheme stamped — an ApiKey write to the same route has no such identity and passes through.
    private static string? ConsoleWriteRoute(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint)
        {
            return null; // no matched route (a 404) — nothing to guard
        }

        var route = endpoint.RoutePattern.RawText;
        return ConsoleWriteEndpoints.Includes([context.Request.Method], route)
            && context.User.Identities.Any(identity => string.Equals(identity.AuthenticationType, ConsoleAuthModule.Scheme, StringComparison.Ordinal))
            ? route
            : null;
    }

    // Appends one audit row for the completed write. Best-effort: the mutation already committed, so a store fault must not
    // turn a successful write into a 500 — it is logged and swallowed. Uses an uncancellable token so a client that
    // disconnected after triggering the write is still audited.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The write already committed; a console-audit append is telemetry that must never fail the request. Any store fault (transient DB error, serialization issue) is logged and the request's own result stands.")]
    private async Task RecordAuditAsync(HttpContext context, string route, string tenantId, string email)
    {
        var entry = new ConsoleAuditEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = email,
            Operation = context.Request.Method,
            Route = route,
            StatusCode = context.Response.StatusCode,
            At = _clock.GetUtcNow(),
        };

        try
        {
            await _audit.RecordAsync(entry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "console-write audit append failed for {Operation} {Route}", entry.Operation, entry.Route);
        }
    }
}
