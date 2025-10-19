namespace Hugo;

/// <summary>
/// Coordinates the completion of a set of asynchronous operations, similar to Go's <c>sync.WaitGroup</c>.
/// </summary>
/// <remarks>
/// Invoke <see cref="Add(int)"/> (or the related overloads) before awaiting <see cref="WaitAsync(CancellationToken)"/>
/// to avoid the same race conditions documented for Go's wait groups.
/// </remarks>
public sealed class WaitGroup
{
    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult(true);
        return tcs;
    }

    private volatile TaskCompletionSource<bool> _tcs = CreateCompletedSource();
    private int _count;

    /// <summary>
    /// Gets the number of outstanding operations tracked by the wait group.
    /// </summary>
    public int Count => Math.Max(Volatile.Read(ref _count), 0);

    /// <summary>
    /// Adds a positive delta to the wait counter.
    /// </summary>
    public void Add(int delta)
    {
        if (delta <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "The delta must be a positive value.");
        }

        var newValue = Interlocked.Add(ref _count, delta);
        if (newValue <= 0)
        {
            Interlocked.Exchange(ref _count, 0);
            throw new InvalidOperationException("WaitGroup counter cannot become negative.");
        }

        GoDiagnostics.RecordWaitGroupAdd(delta);

        if (newValue == delta)
        {
            Interlocked.Exchange(ref _tcs, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    /// <summary>
    /// Tracks the provided task and marks the wait group complete when it finishes.
    /// </summary>
    public void Add(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        Add(1);
        ObserveTask(task);
    }

    /// <summary>
    /// Runs the supplied delegate via <see cref="Task.Run(Action)"/> and tracks it.
    /// </summary>
    public void Go(Func<Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        Add(1);

        Task task;
        try
        {
            task = Task.Run(work, cancellationToken);
        }
        catch
        {
            Done();
            throw;
        }

        ObserveTask(task);
    }

    private void ObserveTask(Task task)
    {
        task.ContinueWith(static (_, state) => ((WaitGroup)state!).Done(), this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Signals that one of the registered operations has completed.
    /// </summary>
    public void Done()
    {
        var newValue = Interlocked.Decrement(ref _count);
        if (newValue < 0)
        {
            Interlocked.Exchange(ref _count, 0);
            throw new InvalidOperationException("WaitGroup counter cannot become negative.");
        }

        GoDiagnostics.RecordWaitGroupDone();

        if (newValue == 0)
        {
            _tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Asynchronously waits for all registered operations to complete.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var awaitable = _tcs.Task;
        if (cancellationToken.CanBeCanceled)
        {
            await awaitable.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await awaitable.ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously waits for all registered operations to complete or for the timeout to elapse.
    /// Returns <c>true</c> when the wait group completed before the timeout expired, otherwise <c>false</c>.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        provider ??= TimeProvider.System;

        if (Count == 0)
        {
            return true;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = WaitAsync(linkedCts.Token);
        if (waitTask.IsCompleted)
        {
            linkedCts.Cancel();
            await waitTask.ConfigureAwait(false);
            return true;
        }

        var delayTask = provider.DelayAsync(timeout, linkedCts.Token);
        var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
        if (completed == waitTask)
        {
            linkedCts.Cancel();
            await waitTask.ConfigureAwait(false);
            return true;
        }

        linkedCts.Cancel();
        return false;
    }
}
