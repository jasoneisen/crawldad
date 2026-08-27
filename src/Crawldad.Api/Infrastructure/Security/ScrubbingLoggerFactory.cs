using Microsoft.Extensions.Logging;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The central scrubbing seam on the logging pipeline: a decorator over the host's <see cref="ILoggerFactory"/>
/// so every category's logger — application, Wolverine, Marten, ASP.NET, any provider — runs its rendered message
/// through the <see cref="CredentialScrubber"/> before it reaches any sink, with no per-call-site discipline required.</summary>
internal sealed class ScrubbingLoggerFactory(ILoggerFactory inner, CredentialScrubber scrubber) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public ILogger CreateLogger(string categoryName) => new ScrubbingLogger(inner.CreateLogger(categoryName), scrubber);

    public void Dispose() => inner.Dispose();
}

/// <summary>One category's scrubbing logger: wraps the message <paramref name="formatter"/> so the rendered string every
/// sink writes is scrubbed. Structured state and any exception are forwarded unchanged — safe because the connect
/// boundary already turns provider faults into secret-free exceptions, so exception messages carry no credentials.</summary>
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
