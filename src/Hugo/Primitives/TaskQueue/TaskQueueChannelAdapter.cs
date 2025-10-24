using System.Threading.Channels;

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
            _pumps[i] = Task.Run(PumpAsync);
            _pumps[i].ContinueWith(HandlePumpCompletion, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Creates an adapter around the provided queue.
    /// </summary>
    public static TaskQueueChannelAdapter<T> Create(
        TaskQueue<T> queue,
        Channel<TaskQueueLease<T>>? channel = null,
        int concurrency = 1,
        bool ownsQueue = false)
    {
        ArgumentNullException.ThrowIfNull(queue);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);

        Channel<TaskQueueLease<T>> leaseChannel = channel ?? Channel.CreateUnbounded<TaskQueueLease<T>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

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
                    if (await TryPublishLeaseAsync(lease).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await _cts.CancelAsync().ConfigureAwait(false);
                    Exception? completionError = await RequeueLeaseAsync(lease, Error.Canceled("Lease could not be delivered before completion.")).ConfigureAwait(false);
                    return completionError;
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    Exception? cancellationError = await RequeueLeaseAsync(lease, Error.Canceled(token: _cts.Token)).ConfigureAwait(false);
                    return cancellationError;
                }
                catch (Exception ex)
                {
                    Exception? requeueError = await RequeueLeaseAsync(lease, Error.FromException(ex)).ConfigureAwait(false);
                    return requeueError ?? ex;
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

    private async ValueTask<bool> TryPublishLeaseAsync(TaskQueueLease<T> lease)
    {
        while (await _leaseChannel.Writer.WaitToWriteAsync(_cts.Token).ConfigureAwait(false))
        {
            if (_leaseChannel.Writer.TryWrite(lease))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<Exception?> RequeueLeaseAsync(TaskQueueLease<T> lease, Error reason)
    {
        try
        {
            await lease.FailAsync(reason, requeue: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        Exception? fault = null;
        try
        {
            Exception?[] results = await Task.WhenAll(_pumps).ConfigureAwait(false);
            fault = results.Aggregate(fault, (current, t) => current ?? t);
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
