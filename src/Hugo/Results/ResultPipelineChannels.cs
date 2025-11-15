using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

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
}
