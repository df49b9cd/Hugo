using System.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests;

public sealed class ResultPipelineRetryLoggingTests
{
    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldLogRecoveryAfterTransientFailure()
    {
        var logger = new ListLogger();
        var timeProvider = new FakeTimeProvider();
        var attempts = 0;

        var result = await ResultPipeline.RetryAsync<int>(
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromResult(Result.Fail<int>(Error.From("first-attempt")))
                    : ValueTask.FromResult(Result.Ok(7));
            },
            maxAttempts: 3,
            initialDelay: TimeSpan.Zero,
            logger: logger,
            timeProvider: timeProvider,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        logger.Events.Count(e => e.EventId == 2002).ShouldBe(1); // failed attempt
        logger.Events.Count(e => e.EventId == 2001).ShouldBe(1); // success after retry
    }

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldLogExhaustionWhenAllAttemptsFail()
    {
        var logger = new ListLogger();
        var timeProvider = new FakeTimeProvider();

        var result = await ResultPipeline.RetryAsync<int>(
            (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.From("permanent-failure"))),
            maxAttempts: 2,
            initialDelay: TimeSpan.Zero,
            logger: logger,
            timeProvider: timeProvider,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        logger.Events.Count(e => e.EventId == 2002).ShouldBe(1); // attempt before exhaustion
        logger.Events.Count(e => e.EventId == 2003).ShouldBe(1); // exhausted attempts
    }

    private sealed class ListLogger : ILogger
    {
        internal sealed record LogEntry(LogLevel Level, int EventId, string Message);

        public IReadOnlyList<LogEntry> Events { get; } = new List<LogEntry>();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Events is List<LogEntry> list)
            {
                list.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
