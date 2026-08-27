using Crawldad.Portal.Auth;
using Microsoft.Extensions.Logging;

namespace Crawldad.Tests.Portal;

public class EmailSenderTests
{
    [Fact]
    public async Task Logging_sender_logs_the_code_at_information()
    {
        var logger = new CollectingLogger<LoggingEmailSender>();
        var sender = new LoggingEmailSender(logger);

        await sender.SendOtpCodeAsync("user@example.com", "ABC234", CancellationToken.None);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain("ABC234");
        entry.Message.ShouldContain("user@example.com");
    }

    [Fact]
    public async Task Unconfigured_sender_fails_closed_and_never_leaks_the_code()
    {
        var sender = new UnconfiguredEmailSender();

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await sender.SendOtpCodeAsync("user@example.com", "ABC234", CancellationToken.None));
    }
}

/// <summary>A minimal <see cref="ILogger{T}"/> that records rendered log entries for assertions.</summary>
internal sealed class CollectingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
