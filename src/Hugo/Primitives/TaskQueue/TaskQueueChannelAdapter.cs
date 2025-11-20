using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Unit = Hugo.Go.Unit;

namespace Hugo;

/// <summary>
/// Adapts a <see cref="TaskQueue{T}"/> to channel semantics with lease-aware acknowledgements.
/// </summary>
/// <typeparam name="T">Work item payload type.</typeparam>
public sealed class TaskQueueChannelAdapter<T> : IAsyncDisposable
{
    private readonly TaskQueue<T> _queue;
    private readonly Channel<TaskQueueLease<T>> _leaseChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task<Exception?>[] _pumps;
    private readonly bool _ownsQueue;
    private int _completedPumps;
    private int _completionSignaled;

    private TaskQueueChannelAdapter(TaskQueue<T> queue, Channel<TaskQueueLease<T>> leaseChannel, int concurrency, bool ownsQueue)
    {
        _queue = queue;
        _leaseChannel = leaseChannel;
        _ownsQueue = ownsQueue;

        _pumps = new Task<Exception?>[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            var pump = Task.Run(PumpAsync);
            _pumps[i] = pump;

            if (pump.IsCompleted)
            {
                HandlePumpCompletion(pump);
            }
            else
            {
                var awaiter = pump.ConfigureAwait(false).GetAwaiter();
                awaiter.UnsafeOnCompleted(() => HandlePumpCompletion(pump));
            }
        }
    }

    /// <summary>Creates an adapter around the provided queue.</summary>
    /// <param name="queue">The queue to adapt.</param>
    /// <param name="channel">The channel used to surface leases; defaults to a bounded channel sized to <paramref name="concurrency"/>.</param>
    /// <param name="concurrency">The number of concurrent lease pumps.</param>
    /// <param name="ownsQueue"><see langword="true"/> to dispose the queue when the adapter is disposed.</param>
    /// <returns>A configured task queue channel adapter.</returns>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory method keeps construction aligned with invariant validation while avoiding public constructors.")]
    public static TaskQueueChannelAdapter<T> Create(
        TaskQueue<T> queue,
        Channel<TaskQueueLease<T>>? channel = null,
        int concurrency = 1,
        bool ownsQueue = false)
    {
        ArgumentNullException.ThrowIfNull(queue);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);

        Channel<TaskQueueLease<T>> leaseChannel = channel ?? Go.BoundedChannel<TaskQueueLease<T>>(concurrency)
            .WithFullMode(BoundedChannelFullMode.Wait)
            .Build();

        return new TaskQueueChannelAdapter<T>(queue, leaseChannel, concurrency, ownsQueue);
    }

    /// <summary>
    /// The channel reader that streams queue leases to consumers.
    /// </summary>
    public ChannelReader<TaskQueueLease<T>> Reader => _leaseChannel.Reader;

    /// <summary>
    /// The underlying queue, exposed for lease lifecycle operations.
    /// </summary>
    public TaskQueue<T> Queue => _queue;

    private async Task<Exception?> PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!await _leaseChannel.Writer.WaitToWriteAsync(_cts.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }

                TaskQueueLease<T> lease;
                try
                {
                    lease = await _queue.LeaseAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    await _cts.CancelAsync().ConfigureAwait(false);
                    return null;
                }

                try
                {
                    if (_leaseChannel.Writer.TryWrite(lease))
                    {
                        continue;
                    }

                    await _cts.CancelAsync().ConfigureAwait(false);
                    Result<Unit> completionResult = await TaskQueueChannelAdapter<T>.RequeueLeaseAsync(lease, Error.Canceled("Lease could not be delivered before completion.")).ConfigureAwait(false);
                    return ToException(completionResult);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    Result<Unit> cancellationResult = await TaskQueueChannelAdapter<T>.RequeueLeaseAsync(lease, Error.Canceled(token: _cts.Token)).ConfigureAwait(false);
                    return ToException(cancellationResult);
                }
                catch (Exception ex)
                {
                    Result<Unit> requeueResult = await TaskQueueChannelAdapter<T>.RequeueLeaseAsync(lease, Error.FromException(ex)).ConfigureAwait(false);
                    Exception? requeueException = ToException(requeueResult);
                    return requeueException ?? ex;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected during disposal
        }
        catch (Exception ex)
        {
            return ex;
        }

        return null;
    }

    private static async ValueTask<Result<Unit>> RequeueLeaseAsync(TaskQueueLease<T> lease, Error reason)
    {
        Result<Unit> result = await Result.TryAsync<Unit>(
            async _ =>
            {
                await lease.FailAsync(reason, requeue: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return Unit.Value;
            },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        if (result.IsFailure && result.Error?.Cause is ObjectDisposedException)
        {
            return Result.Ok(Unit.Value);
        }

        return result;
    }

    private static Exception? ToException(Result<Unit> result) =>
        result.Finally(static _ => (Exception?)null, static error => error.Cause ?? new InvalidOperationException(error.Message));

    private void HandlePumpCompletion(Task<Exception?> pumpTask)
    {
        Exception? error = null;

        try
        {
            if (pumpTask.IsFaulted)
            {
                error = pumpTask.Exception?.GetBaseException();
            }
            else if (!pumpTask.IsCanceled)
            {
                error = pumpTask.Result;
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error is not null)
        {
            _cts.Cancel();
            SignalCompletion(error);
            return;
        }

        if (Interlocked.Increment(ref _completedPumps) == _pumps.Length)
        {
            SignalCompletion(null);
        }
    }

    private void SignalCompletion(Exception? error)
    {
        if (Interlocked.CompareExchange(ref _completionSignaled, 1, 0) != 0)
        {
            return;
        }

        if (error is not null)
        {
            _leaseChannel.Writer.TryComplete(error);
        }
        else
        {
            _leaseChannel.Writer.TryComplete();
        }
    }

    /// <summary>Disposes the adapter, stops lease pumps, and optionally disposes the underlying queue.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        Exception? fault = null;
        try
        {
            Exception?[] results = await Task.WhenAll(_pumps).ConfigureAwait(false);
            for (int i = 0; i < results.Length && fault is null; i++)
            {
                fault = results[i];
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            fault ??= ex;
        }

        SignalCompletion(fault);

        _cts.Dispose();

        if (_ownsQueue)
        {
            await _queue.DisposeAsync().ConfigureAwait(false);
        }
    }
}
