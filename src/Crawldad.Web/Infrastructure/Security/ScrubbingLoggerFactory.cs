using Microsoft.Extensions.Logging;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>
/// The central scrubbing seam on the logging pipeline (§12, WP3): a decorator over the host's <see cref="ILoggerFactory"/>
/// so <b>every</b> category's logger — application, Wolverine, Marten, ASP.NET, any provider — runs its rendered message
/// through the <see cref="CredentialScrubber"/> before it reaches any sink. Because it wraps the factory (the one point
/// every <see cref="ILogger"/> and <see cref="ILogger{T}"/> is created from) rather than any single call site, a
/// framework log that echoes a connect string is scrubbed exactly like an application log, with no per-call-site
/// discipline required.
/// </summary>
/// <param name="inner">The real logger factory that fans out to the registered providers.</param>
/// <param name="scrubber">The credential scrubber applied to every rendered message.</param>
internal sealed class ScrubbingLoggerFactory(ILoggerFactory inner, CredentialScrubber scrubber) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public ILogger CreateLogger(string categoryName) => new ScrubbingLogger(inner.CreateLogger(categoryName), scrubber);

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// One category's scrubbing logger: delegates enablement and scopes to the inner logger, but wraps the message
/// <paramref name="formatter"/> so the string every sink renders is scrubbed. The structured <c>state</c> and any
/// exception are forwarded unchanged (the connect boundary guarantees exception messages are already secret-free, §12);
/// what a text sink writes as the log line is what passes through the scrubber.
/// </summary>
/// <param name="inner">The underlying logger the factory produced for this category.</param>
/// <param name="scrubber">The credential scrubber applied to the rendered message.</param>
internal sealed class ScrubbingLogger(ILogger inner, CredentialScrubber scrubber) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        inner.Log(logLevel, eventId, state, exception, (s, e) => scrubber.Scrub(formatter(s, e)));
    }
}
