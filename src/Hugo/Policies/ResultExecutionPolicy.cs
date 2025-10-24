using System.Globalization;

namespace Hugo.Policies;

/// <summary>
/// Encapsulates retry and compensation behavior for multi-step result pipelines.
/// </summary>
public sealed record ResultExecutionPolicy(
    ResultRetryPolicy? Retry = null,
    ResultCompensationPolicy? Compensation = null)
{
    public static ResultExecutionPolicy None { get; } = new();

    public ResultExecutionPolicy WithRetry(ResultRetryPolicy retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        return this with { Retry = retry };
    }

    public ResultExecutionPolicy WithCompensation(ResultCompensationPolicy compensation)
    {
        ArgumentNullException.ThrowIfNull(compensation);
        return this with { Compensation = compensation };
    }

    internal ResultRetryPolicy EffectiveRetry => Retry ?? ResultRetryPolicy.None;

    internal ResultCompensationPolicy EffectiveCompensation => Compensation ?? ResultCompensationPolicy.None;
}

/// <summary>
/// Defines when a failed operation should be retried.
/// </summary>
public sealed class ResultRetryPolicy
{
    private readonly Func<RetryState, CancellationToken, ValueTask<RetryDecision>> _scheduler;
    private readonly Func<TimeProvider, RetryState> _stateFactory;

    private ResultRetryPolicy(Func<RetryState, CancellationToken, ValueTask<RetryDecision>> scheduler, Func<TimeProvider, RetryState> stateFactory)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _stateFactory = stateFactory ?? throw new ArgumentNullException(nameof(stateFactory));
    }

    public static ResultRetryPolicy None { get; } = new(
        static (_, _) => ValueTask.FromResult(RetryDecision.Stop),
        static provider => new RetryState(provider, 0, TimeSpan.Zero, 1.0, null)
    );

    public static ResultRetryPolicy FixedDelay(int maxAttempts, TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        return new ResultRetryPolicy(
            static (state, _) =>
            {
                if (state.Attempt < state.MaxAttempts)
                {
                    return ValueTask.FromResult(RetryDecision.RetryAfter(state.BaseDelay));
                }

                return ValueTask.FromResult(RetryDecision.Stop);
            },
            provider => new RetryState(provider, maxAttempts, delay, 1.0, null)
        );
    }

    public static ResultRetryPolicy Exponential(int maxAttempts, TimeSpan initialDelay, double multiplier = 2.0, TimeSpan? maxDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(initialDelay, TimeSpan.Zero);

        if (multiplier <= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Multiplier must be greater than 1.0.");
        }

        if (maxDelay is { } upper && upper < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay));
        }

        return new ResultRetryPolicy(
            static (state, _) =>
            {
                if (state.Attempt < state.MaxAttempts)
                {
                    var exponent = Math.Pow(state.Multiplier, Math.Max(0, state.Attempt - 1));
                    var proposedTicks = state.BaseDelay.Ticks * exponent;
                    var cappedTicks = state.MaxDelay.HasValue
                        ? Math.Min(state.MaxDelay.Value.Ticks, proposedTicks)
                        : proposedTicks;

                    var delay = TimeSpan.FromTicks((long)Math.Min(long.MaxValue, Math.Max(0, cappedTicks)));
                    return ValueTask.FromResult(RetryDecision.RetryAfter(delay));
                }

                return ValueTask.FromResult(RetryDecision.Stop);
            },
            provider => new RetryState(provider, maxAttempts, initialDelay, multiplier, maxDelay)
        );
    }

    public static ResultRetryPolicy Cron(string expression, TimeZoneInfo? timeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var schedule = CronSchedule.Parse(expression, timeZone ?? TimeZoneInfo.Utc);

        return new ResultRetryPolicy(
            (state, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = state.TimeProvider.GetUtcNow();
                var next = schedule.GetNextOccurrence(now, state.TimeProvider);
                return ValueTask.FromResult(next is null ? RetryDecision.Stop : RetryDecision.RetryAt(next.Value));
            },
            provider => new RetryState(provider, int.MaxValue, TimeSpan.Zero, 1.0, null)
        );
    }

    public RetryState CreateState(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return _stateFactory(timeProvider);
    }

    public ValueTask<RetryDecision> EvaluateAsync(RetryState state, Error error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.RegisterFailure(error);
        return _scheduler(state, cancellationToken);
    }
}

/// <summary>
/// Determines how compensating actions are executed when a pipeline partially fails.
/// </summary>
public sealed class ResultCompensationPolicy(Func<CompensationContext, ValueTask> executor)
{
    public static ResultCompensationPolicy None { get; } = new(static _ => ValueTask.CompletedTask);

    private readonly Func<CompensationContext, ValueTask> _executor = executor ?? throw new ArgumentNullException(nameof(executor));

    /// <summary>
    /// Executes the configured compensation strategy using the supplied <paramref name="context"/>.
    /// </summary>
    public ValueTask ExecuteAsync(CompensationContext context) => _executor(context);

    public static ResultCompensationPolicy SequentialReverse => new(static context => context.ExecuteAsync());

    public static ResultCompensationPolicy Parallel => new(static context => context.ExecuteAsync(parallel: true));
}

/// <summary>
/// Represents a decision about whether to retry a failed operation.
/// </summary>
public readonly record struct RetryDecision
{
    private RetryDecision(bool shouldRetry, TimeSpan? delay, DateTimeOffset? schedule)
    {
        ShouldRetry = shouldRetry;
        Delay = delay;
        ScheduledAt = schedule;
    }

    public bool ShouldRetry { get; }

    public TimeSpan? Delay { get; }

    public DateTimeOffset? ScheduledAt { get; }

    public static RetryDecision Stop => new(false, null, null);

    public static RetryDecision RetryAfter(TimeSpan delay) => new(true, delay, null);

    public static RetryDecision RetryAt(DateTimeOffset scheduledAt) => new(true, null, scheduledAt);
}

/// <summary>
/// Tracks retry attempt information and timing.
/// </summary>
public sealed class RetryState(TimeProvider timeProvider, int maxAttempts, TimeSpan baseDelay, double multiplier, TimeSpan? maxDelay)
{
    private List<Error>? _errors;

    public TimeProvider TimeProvider { get; } = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public int MaxAttempts { get; } = maxAttempts;

    public TimeSpan BaseDelay { get; } = baseDelay;

    public double Multiplier { get; } = multiplier;

    public TimeSpan? MaxDelay { get; } = maxDelay;

    public int Attempt { get; private set; }

    public string ActivityId { get; private set; } = string.Empty;

    public Error? LastError { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public IReadOnlyList<Error> Errors => _errors ?? (IReadOnlyList<Error>)Array.Empty<Error>();

    public void RegisterFailure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        Attempt++;
        LastError = error;
        LastAttemptAt = TimeProvider.GetUtcNow();

        (_errors ??= new List<Error>(capacity: 4)).Add(error);
    }

    public void SetActivityId(string? activityId) => ActivityId = activityId ?? string.Empty;

    public void Reset()
    {
        Attempt = 0;
        LastError = null;
        LastAttemptAt = null;
        _errors?.Clear();
    }
}

/// <summary>
/// Provides context and helpers for executing compensation actions.
/// </summary>
public sealed class CompensationContext(Stack<CompensationAction> actions, CancellationToken cancellationToken)
{
    private readonly Stack<CompensationAction> _actions = actions;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// Replays the registered compensation actions sequentially or in parallel.
    /// </summary>
    public ValueTask ExecuteAsync(bool parallel = false)
    {
        if (!parallel)
        {
            return ExecuteSequentialAsync(CancellationToken);
        }

        return ExecuteParallelAsync(CancellationToken);
    }

    private async ValueTask ExecuteSequentialAsync(CancellationToken cancellationToken)
    {
        while (_actions.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = _actions.Pop();
            await action.Callback(action.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ExecuteParallelAsync(CancellationToken cancellationToken)
    {
        if (_actions.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>(_actions.Count);
        while (_actions.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = _actions.Pop();
            tasks.Add(Task.Run(() => action.Callback(action.Payload, cancellationToken), cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

/// <summary>
/// Represents a compensation action registered during pipeline execution.
/// </summary>
public readonly record struct CompensationAction(Func<object?, CancellationToken, ValueTask> Callback, object? Payload);

/// <summary>
/// Collects compensation actions emitted during pipeline execution.
/// </summary>
public sealed class CompensationScope
{
    private readonly Stack<CompensationAction> _actions = new();
    private readonly Lock _sync = new();

    public bool HasActions
    {
        get
        {
            lock (_sync)
            {
                return _actions.Count > 0;
            }
        }
    }

    public void Register(Func<CancellationToken, ValueTask> compensation)
    {
        ArgumentNullException.ThrowIfNull(compensation);

        lock (_sync)
        {
            _actions.Push(new CompensationAction(static (payload, cancellationToken) =>
            {
                return ((Func<CancellationToken, ValueTask>)payload!)(cancellationToken);
            }, compensation));
        }
    }

    public void Register<TState>(TState state, Func<TState, CancellationToken, ValueTask> compensation)
    {
        ArgumentNullException.ThrowIfNull(compensation);

        lock (_sync)
        {
            _actions.Push(new CompensationAction(static (payload, cancellationToken) =>
            {
                var (capturedState, callback) = ((TState State, Func<TState, CancellationToken, ValueTask> Callback))payload!;
                return callback(capturedState, cancellationToken);
            }, (state, compensation)));
        }
    }

    internal Stack<CompensationAction> Capture()
    {
        lock (_sync)
        {
            if (_actions.Count == 0)
            {
                return new Stack<CompensationAction>();
            }

            var snapshot = new Stack<CompensationAction>(_actions.Count);
            var items = _actions.ToArray();
            for (var i = items.Length - 1; i >= 0; i--)
            {
                snapshot.Push(items[i]);
            }

            _actions.Clear();
            return snapshot;
        }
    }

    internal void Absorb(CompensationScope other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (ReferenceEquals(this, other))
        {
            return;
        }

        var captured = other.Capture();
        if (captured.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            while (captured.Count > 0)
            {
                _actions.Push(captured.Pop());
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _actions.Clear();
        }
    }
}

/// <summary>
/// Minimal cron implementation for scheduling retries.
/// </summary>
internal sealed class CronSchedule
{
    private readonly string _expression;
    private readonly TimeZoneInfo _timeZone;

    private CronSchedule(string expression, TimeZoneInfo timeZone)
    {
        _expression = expression;
        _timeZone = timeZone;
    }

    public static CronSchedule Parse(string expression, TimeZoneInfo timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new CronSchedule(expression, timeZone);
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset reference, TimeProvider provider)
    {
        var local = TimeZoneInfo.ConvertTime(reference, _timeZone);
        var segments = _expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is not (5 or 6))
        {
            throw new FormatException("Cron expression must contain 5 or 6 segments.");
        }

        var minute = ParseField(segments[1], local.Minute, 0, 59);
        var hour = ParseField(segments[0], local.Hour, 0, 23);
        var day = ParseField(segments[2], local.Day, 1, DateTime.DaysInMonth(local.Year, local.Month));
        var month = ParseField(segments[3], local.Month, 1, 12);

        ArgumentNullException.ThrowIfNull(provider);

        var dayOfMonth = Math.Min(day, DateTime.DaysInMonth(local.Year, month));
        var nextLocal = new DateTimeOffset(local.Year, month, dayOfMonth, hour, minute, 0, local.Offset);
        if (nextLocal <= local)
        {
            nextLocal = nextLocal.AddMinutes(1);
        }

        return nextLocal.ToUniversalTime();
    }

    private static int ParseField(string segment, int current, int min, int max)
    {
        if (segment == "*")
        {
            return current;
        }

        if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return Math.Clamp(value, min, max);
        }

        if (segment.StartsWith("*/", StringComparison.Ordinal))
        {
            if (int.TryParse(segment.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var step) && step > 0)
            {
                var next = current + step;
                return next > max ? min : next;
            }
        }

        throw new FormatException($"Unsupported cron segment '{segment}'.");
    }
}
