using System.Diagnostics.CodeAnalysis;

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
    private const TaskCreationOptions DefaultGoOptions = TaskCreationOptions.DenyChildAttach;

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

    /// <summary>Adds a positive delta to the wait counter.</summary>
    /// <param name="delta">The number of operations to register with the wait group.</param>
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

    /// <summary>Tracks the provided task and marks the wait group complete when it finishes.</summary>
    /// <param name="task">The task to observe.</param>
    public void Add(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        Add(1);
        ObserveTask(task);
    }

    /// <summary>
    /// Runs the supplied delegate via <see cref="Go.Run(Func{Task}, TaskScheduler?, TaskCreationOptions)"/> and tracks it.
    /// </summary>
    /// <param name="work">The asynchronous delegate to execute.</param>
    /// <param name="cancellationToken">The token used to cancel the queued work.</param>
    /// <param name="scheduler">Optional scheduler that controls how the work is dispatched.</param>
    /// <param name="creationOptions">Task creation flags applied to the scheduled work.</param>
    [SuppressMessage("Design", "CA1068:CancellationTokenParametersShouldComeLast", Justification = "Scheduler and creation options must remain optional parameters for backward compatibility.")]
    public void Go(
        Func<Task> work,
        CancellationToken cancellationToken = default,
        TaskScheduler? scheduler = null,
        TaskCreationOptions creationOptions = DefaultGoOptions)
    {
        ArgumentNullException.ThrowIfNull(work);

        Add(1);

        Task task;
        try
        {
            task = global::Hugo.Go.Run(_ => work(), cancellationToken, scheduler, creationOptions);
        }
        catch
        {
            Done();
            throw;
        }

        ObserveTask(task);
    }

    /// <summary>
    /// Tracks an already created <see cref="Task"/> without re-scheduling it.
    /// </summary>
    /// <param name="task">The task to observe.</param>
    public void Go(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Add(task);
    }

    /// <summary>
    /// Tracks an existing <see cref="ValueTask"/> without allocating an additional <see cref="Task.Run(Action)"/> wrapper.
    /// </summary>
    /// <param name="task">The value task to observe.</param>
    public void Go(ValueTask task)
    {
        Go(global::Hugo.Go.Run(task));
    }

    private void ObserveTask(Task task) => task.ContinueWith(static (_, state) => ((WaitGroup)state!).Done(), this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>Signals that one of the registered operations has completed.</summary>
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

    /// <summary>Asynchronously waits for all registered operations to complete.</summary>
    /// <param name="cancellationToken">The token used to cancel the wait.</param>
    /// <returns>A task that completes when all operations finish.</returns>
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
    /// <param name="timeout">The amount of time to wait before timing out.</param>
    /// <param name="provider">The time provider used to schedule the timeout.</param>
    /// <param name="cancellationToken">The token used to cancel the wait.</param>
    /// <returns><see langword="true"/> when the wait group completed before the timeout elapsed; otherwise <see langword="false"/>.</returns>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        provider ??= TimeProvider.System;

        if (Count == 0)
        {
            return true;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = WaitAsync(linkedCts.Token);
        if (waitTask.IsCompleted)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await waitTask.ConfigureAwait(false);
            return true;
        }

        var delayTask = provider.DelayAsync(timeout, linkedCts.Token);
        var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
        if (completed == waitTask)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await waitTask.ConfigureAwait(false);
            return true;
        }

        await linkedCts.CancelAsync().ConfigureAwait(false);
        return false;
    }
}
