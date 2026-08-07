using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The logging-pipeline decorator (§12, WP3): <see cref="ScrubbingLoggerFactory"/>/<see cref="ScrubbingLogger"/> route
/// every rendered message through the <see cref="CredentialScrubber"/> while delegating enablement, scopes, and factory
/// lifecycle to the inner pipeline — so the central chokepoint scrubs without altering logging behaviour.
/// </summary>
public class ScrubbingLoggerTests
{
    private const string _redacted = CredentialScrubber.Redaction;

    private static CredentialScrubber Scrubber(params string[] secrets) => new(new StubSecretScope(secrets));

    [Fact]
    public void The_rendered_message_reaching_the_sink_is_scrubbed()
    {
        var inner = new RecordingLogger();
        var logger = new ScrubbingLogger(inner, Scrubber());

        logger.Log(LogLevel.Warning, new EventId(7), "connect wss://h/x?token=tok_SECRET_123", null, static (s, _) => s);

        // The inner sink renders via the wrapped formatter → it sees the scrubbed line.
        inner.LastMessage.ShouldBe($"connect wss://h/x?token={_redacted}");
        inner.LastLevel.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public void An_exact_live_secret_in_a_framework_message_is_scrubbed()
    {
        const string Secret = "bb_live_LEAKCANARY_framework_0123456789";
        var inner = new RecordingLogger();
        var logger = new ScrubbingLogger(inner, Scrubber(Secret));

        logger.Log(LogLevel.Information, default, $"session created for {Secret}", null, static (s, _) => s);

        inner.LastMessage.ShouldBe($"session created for {_redacted}");
    }

    [Fact]
    public void IsEnabled_and_BeginScope_delegate_to_the_inner_logger()
    {
        var inner = new RecordingLogger { Enabled = false };
        var logger = new ScrubbingLogger(inner, Scrubber());

        logger.IsEnabled(LogLevel.Error).ShouldBeFalse();

        using (logger.BeginScope("scope-state"))
        {
            inner.LastScope.ShouldBe("scope-state");
        }
    }

    [Fact]
    public void The_factory_wraps_loggers_and_delegates_lifecycle()
    {
        var innerFactory = new RecordingLoggerFactory();
        using var factory = new ScrubbingLoggerFactory(innerFactory, Scrubber());

        var logger = factory.CreateLogger("Some.Category");
        logger.ShouldBeOfType<ScrubbingLogger>();
        innerFactory.CreatedCategory.ShouldBe("Some.Category");

        var provider = new CapturingLoggerProvider();
        factory.AddProvider(provider);
        innerFactory.AddedProvider.ShouldBeSameAs(provider);

        factory.Dispose();
        innerFactory.Disposed.ShouldBeTrue();
    }

    private sealed class RecordingLogger : ILogger
    {
        public bool Enabled { get; init; } = true;

        public string? LastMessage { get; private set; }

        public LogLevel LastLevel { get; private set; }

        public object? LastScope { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            LastScope = state;
            return new Noop();
        }

        public bool IsEnabled(LogLevel logLevel) => Enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LastLevel = logLevel;
            LastMessage = formatter(state, exception); // render via the (wrapped) formatter, exactly as a real sink does
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
                // Nothing to release.
            }
        }
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public string? CreatedCategory { get; private set; }

        public ILoggerProvider? AddedProvider { get; private set; }

        public bool Disposed { get; private set; }

        public void AddProvider(ILoggerProvider provider) => AddedProvider = provider;

        public ILogger CreateLogger(string categoryName)
        {
            CreatedCategory = categoryName;
            return new RecordingLogger();
        }

        public void Dispose() => Disposed = true;
    }
}
