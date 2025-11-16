using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

using Hugo.Policies;

using Unit = Hugo.Go.Unit;

namespace Hugo;

/// <summary>
/// Provides pipeline-aware adapters for Go channel helpers so compensation scopes remain aligned with the result pipeline.
/// </summary>
public static class ResultPipelineChannels
{
    public static async ValueTask<Result<TResult>> SelectAsync<TResult>(
        ResultPipelineStepContext context,
        IEnumerable<ChannelCase<TResult>> cases,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cases);

        ChannelCase<TResult>[] prepared = WrapCases(context, cases);
        TimeSpan effectiveTimeout = timeout ?? Timeout.InfiniteTimeSpan;
        var provider = context.TimeProvider;

        CancellationTokenSource? linkedCts = null;
        var effectiveToken = LinkTokens(context.CancellationToken, cancellationToken, out linkedCts);

        try
        {
            Result<TResult> result = effectiveTimeout == Timeout.InfiniteTimeSpan
                ? await Go.SelectAsync(provider, effectiveToken, prepared).ConfigureAwait(false)
                : await Go.SelectAsync(effectiveTimeout, provider, effectiveToken, prepared).ConfigureAwait(false);

            context.AbsorbResult(result);
            return result;
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    public static ValueTask<Result<Unit>> FanInAsync<T>(
        ResultPipelineStepContext context,
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, ValueTask<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = WrapHandler(context, onValue);
        return FanInInternal(context, readers, handler, timeout, cancellationToken);
    }

    public static ValueTask<Result<Unit>> FanInAsync<T>(
        ResultPipelineStepContext context,
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, Task<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return FanInAsync(
            context,
            readers,
            (value, token) => new ValueTask<Result<Unit>>(onValue(value, token)),
            timeout,
            cancellationToken);
    }

    public static ValueTask<Result<Unit>> FanInAsync<T>(
        ResultPipelineStepContext context,
        IEnumerable<ChannelReader<T>> readers,
        Func<T, Task<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return FanInAsync(
            context,
            readers,
            (value, token) => new ValueTask<Result<Unit>>(onValue(value)),
            timeout,
            cancellationToken);
    }

    public static ValueTask<Result<Unit>> FanInAsync<T>(
        ResultPipelineStepContext context,
        IEnumerable<ChannelReader<T>> readers,
        Func<T, ValueTask<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return FanInAsync(
            context,
            readers,
            (value, token) => onValue(value),
            timeout,
            cancellationToken);
    }

    public static async ValueTask<Result<Unit>> MergeAsync<T>(
        ResultPipelineStepContext context,
        IEnumerable<ChannelReader<T>> readers,
        ChannelWriter<T> destination,
        bool completeDestination = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(destination);

        CancellationTokenSource? linkedCts = null;
        var effectiveToken = LinkTokens(context.CancellationToken, cancellationToken, out linkedCts);

        try
        {
            var result = await Go.FanInAsync(readers, destination, completeDestination, timeout ?? Timeout.InfiniteTimeSpan, context.TimeProvider, effectiveToken).ConfigureAwait(false);
            context.AbsorbResult(result);
            return result;
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    public static async ValueTask<Result<Unit>> MergeWithStrategyAsync<T>(
        ResultPipelineStepContext context,
        IReadOnlyList<ChannelReader<T>> readers,
        ChannelWriter<T> destination,
        Func<IReadOnlyList<ChannelReader<T>>, CancellationToken, ValueTask<int>> selectionStrategy,
        bool completeDestination = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(selectionStrategy);

        CancellationTokenSource? linkedCts = null;
        var token = LinkTokens(context.CancellationToken, cancellationToken, out linkedCts);

        try
        {
            var completed = new bool[readers.Count];
            int remaining = readers.Count;

            while (remaining > 0 && !token.IsCancellationRequested)
            {
                int index = await selectionStrategy(readers, token).ConfigureAwait(false);
                if (index < 0 || index >= readers.Count)
                {
                    await Task.Yield();
                    continue;
                }

                if (completed[index])
                {
                    continue;
                }

                try
                {
                    var value = await readers[index].ReadAsync(token).ConfigureAwait(false);
                    await destination.WriteAsync(value, token).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    completed[index] = true;
                    remaining--;
                }
            }

            if (completeDestination)
            {
                destination.TryComplete();
            }

            var success = Result.Ok(Unit.Value);
            context.AbsorbResult(success);
            return success;
        }
        catch (OperationCanceledException oce)
        {
            if (completeDestination)
            {
                destination.TryComplete(oce);
            }

            var canceled = Result.Fail<Unit>(Error.Canceled(token: oce.CancellationToken));
            context.AbsorbResult(canceled);
            return canceled;
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    public static async ValueTask<Result<Unit>> BroadcastAsync<T>(
        ResultPipelineStepContext context,
        ChannelReader<T> source,
        IReadOnlyList<ChannelWriter<T>> destinations,
        bool completeDestinations = true,
        TimeSpan? deadline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinations);

        CancellationTokenSource? linkedCts = null;
        var effectiveToken = LinkTokens(context.CancellationToken, cancellationToken, out linkedCts);

        try
        {
            var result = await Go.FanOutAsync(
                source,
                destinations,
                completeDestinations,
                deadline,
                context.TimeProvider,
                effectiveToken).ConfigureAwait(false);

            context.AbsorbResult(result);
            return result;
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    public static ChannelReader<IReadOnlyList<T>> WindowAsync<T>(
        ResultPipelineStepContext context,
        ChannelReader<T> source,
        int batchSize,
        TimeSpan flushInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (flushInterval < TimeSpan.Zero && flushInterval != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(flushInterval));
        }

        var output = Channel.CreateUnbounded<IReadOnlyList<T>>();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        var token = linkedCts.Token;
        var provider = context.TimeProvider;

        _ = Go.Run(async _ =>
        {
            var buffer = new List<T>(batchSize);

            try
            {
                while (true)
                {
                    var raceResult = await Go.RaceValueTaskAsync(
                        new Func<CancellationToken, ValueTask<Result<WindowRaceOutcome>>>[]
                        {
                            async ct =>
                            {
                                try
                                {
                                    var hasData = await source.WaitToReadAsync(ct).ConfigureAwait(false);
                                    return Result.Ok(new WindowRaceOutcome(WindowRaceWinner.ReaderReady, hasData));
                                }
                                catch (OperationCanceledException oce)
                                {
                                    return Result.Fail<WindowRaceOutcome>(Error.Canceled(token: oce.CancellationToken));
                                }
                            },
                            async ct =>
                            {
                                try
                                {
                                    await CreateDelayTask(provider, flushInterval, ct).ConfigureAwait(false);
                                    return Result.Ok(new WindowRaceOutcome(WindowRaceWinner.TimerElapsed, hasData: false));
                                }
                                catch (OperationCanceledException oce)
                                {
                                    return Result.Fail<WindowRaceOutcome>(Error.Canceled(token: oce.CancellationToken));
                                }
                            }
                        },
                        cancellationToken: token,
                        timeProvider: provider).ConfigureAwait(false);

                    if (raceResult.IsFailure)
                    {
                        if (raceResult.Error?.Code == ErrorCodes.Canceled)
                        {
                            throw new OperationCanceledException(token);
                        }

                        output.Writer.TryComplete(new ResultException(raceResult.Error!));
                        return;
                    }

                    var outcome = raceResult.Value;
                    if (outcome.Winner == WindowRaceWinner.TimerElapsed)
                    {
                        if (buffer.Count > 0)
                        {
                            await output.Writer.WriteAsync(buffer.ToArray(), token).ConfigureAwait(false);
                            buffer.Clear();
                        }

                        continue;
                    }

                    if (!outcome.HasData)
                    {
                        break;
                    }

                    while (source.TryRead(out var item))
                    {
                        buffer.Add(item);
                        if (buffer.Count >= batchSize)
                        {
                            await output.Writer.WriteAsync(buffer.ToArray(), token).ConfigureAwait(false);
                            buffer.Clear();
                        }
                    }
                }

                if (buffer.Count > 0)
                {
                    await output.Writer.WriteAsync(buffer.ToArray(), token).ConfigureAwait(false);
                }

                output.Writer.TryComplete();
            }
            catch (OperationCanceledException oce)
            {
                output.Writer.TryComplete(oce);
            }
            finally
            {
                linkedCts.Dispose();
            }
        }, CancellationToken.None);

        return output.Reader;
    }

    public static IReadOnlyList<ChannelReader<T>> FanOut<T>(
        ResultPipelineStepContext context,
        ChannelReader<T> source,
        int branchCount,
        bool completeBranches = true,
        TimeSpan? deadline = null,
        Func<int, Channel<T>>? channelFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);

        var (token, linkedCts) = LinkTokensForFanOut(context.CancellationToken, cancellationToken);
        try
        {
            var readers = Go.FanOut(source, branchCount, completeBranches, deadline, context.TimeProvider, channelFactory, token);
            context.RegisterCompensation(readers, static async (collection, ct) =>
            {
                foreach (var reader in collection)
                {
                    try
                    {
                        await reader.Completion.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
            return readers;
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    public static ResultPipelineSelectBuilder<TResult> Select<TResult>(
        ResultPipelineStepContext context,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var duration = timeout ?? Timeout.InfiniteTimeSpan;
        return new ResultPipelineSelectBuilder<TResult>(context, duration, provider ?? context.TimeProvider, cancellationToken);
    }

    private static ValueTask<bool> CreateDelayTask(TimeProvider provider, TimeSpan flushInterval, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(token);
        }

        if (flushInterval == Timeout.InfiniteTimeSpan)
        {
            return AwaitInfiniteDelay(token);
        }

        var dueTime = flushInterval <= TimeSpan.Zero ? TimeSpan.Zero : flushInterval;
        return new WindowDelay(provider, dueTime, token).Task;
    }

    private static async ValueTask<bool> AwaitInfiniteDelay(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        return true;
    }

    private enum WindowRaceWinner
    {
        ReaderReady,
        TimerElapsed
    }

    private readonly struct WindowRaceOutcome
    {
        public WindowRaceOutcome(WindowRaceWinner winner, bool hasData)
        {
            Winner = winner;
            HasData = hasData;
        }

        public WindowRaceWinner Winner { get; }

        public bool HasData { get; }
    }

    private static ChannelCase<TResult>[] WrapCases<TResult>(ResultPipelineStepContext context, IEnumerable<ChannelCase<TResult>> cases)
    {
        if (cases is ChannelCase<TResult>[] caseArray)
        {
            return DecorateCases(context, caseArray);
        }

        var list = cases.ToArray();
        return DecorateCases(context, list);
    }

    private static ChannelCase<TResult>[] DecorateCases<TResult>(ResultPipelineStepContext context, IReadOnlyList<ChannelCase<TResult>> cases)
    {
        var wrapped = new ChannelCase<TResult>[cases.Count];
        for (int i = 0; i < cases.Count; i++)
        {
            var original = cases[i];
            wrapped[i] = original.WithContinuation(async (state, token) =>
            {
                var result = await original.ContinueWithAsync(state, token).ConfigureAwait(false);
                context.AbsorbResult(result);
                return result;
            });
        }

        return wrapped;
    }

    private static Func<T, CancellationToken, ValueTask<Result<Unit>>> WrapHandler<T>(
        ResultPipelineStepContext context,
        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler) =>
        async (value, token) =>
        {
            var result = await handler(value, token).ConfigureAwait(false);
            context.AbsorbResult(result);
            return result;
        };

    private static async ValueTask<Result<Unit>> FanInInternal<T>(
        ResultPipelineStepContext context,
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? linkedCts = null;
        var effectiveToken = LinkTokens(context.CancellationToken, cancellationToken, out linkedCts);

        try
        {
            var result = await Go.SelectFanInValueTaskAsync(readers, handler, timeout, context.TimeProvider, effectiveToken).ConfigureAwait(false);
            context.AbsorbResult(result);
            return result;
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    [SuppressMessage("Design", "CA1068:CancellationTokenParametersShouldComeLast", Justification = "Private helper that links tokens for pipeline adapters.")]
    private static CancellationToken LinkTokens(CancellationToken primary, CancellationToken secondary, out CancellationTokenSource? linkedCts)
    {
        linkedCts = null;
        if (!secondary.CanBeCanceled)
        {
            return primary;
        }

        if (!primary.CanBeCanceled)
        {
            return secondary;
        }

        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(primary, secondary);
        return linkedCts.Token;
    }

    [SuppressMessage("Design", "CA1068:CancellationTokenParametersShouldComeLast", Justification = "Private helper that links tokens for pipeline adapters.")]
    private static (CancellationToken Token, CancellationTokenSource? Source) LinkTokensForFanOut(CancellationToken primary, CancellationToken secondary)
    {
        if (!secondary.CanBeCanceled)
        {
            return (primary, null);
        }

        if (!primary.CanBeCanceled)
        {
            return (secondary, null);
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(primary, secondary);
        return (linked.Token, linked);
    }

    private sealed class WindowDelay : IValueTaskSource<bool>
    {
        private ManualResetValueTaskSourceCore<bool> _core;
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _registration;
        private readonly ITimer _timer;
        private int _completed;

        internal WindowDelay(TimeProvider provider, TimeSpan dueTime, CancellationToken token)
        {
            _token = token;
            _core.RunContinuationsAsynchronously = true;
            _core.Reset();
            _registration = token.Register(static state => ((WindowDelay)state!).OnCanceled(), this);
            _timer = provider.CreateTimer(static state => ((WindowDelay)state!).OnElapsed(), this, dueTime, Timeout.InfiniteTimeSpan);
        }

        internal ValueTask<bool> Task => new(this, _core.Version);

        private void OnElapsed()
        {
            if (!TryComplete())
            {
                return;
            }

            _core.SetResult(true);
        }

        private void OnCanceled()
        {
            if (!TryComplete())
            {
                return;
            }

            _core.SetException(new OperationCanceledException(_token));
        }

        private bool TryComplete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return false;
            }

            _timer.Dispose();
            _registration.Dispose();
            return true;
        }

        bool IValueTaskSource<bool>.GetResult(short token) => _core.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}
