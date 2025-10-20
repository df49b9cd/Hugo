namespace Hugo;

using Unit = Hugo.Go.Unit;

/// <summary>
/// Coordinates asynchronous operations, propagating the first failure and cancelling remaining work, similar to Go's errgroup package.
/// </summary>
public sealed class ErrGroup : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly WaitGroup _waitGroup = new();
    private Error? _error;

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrGroup"/> class.
    /// </summary>
    /// <param name="cancellationToken">An optional cancellation token that cancels the group when signaled.</param>
    public ErrGroup(CancellationToken cancellationToken = default)
    {
        _cts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
    }

    /// <summary>
    /// Gets a cancellation token that is signaled when the group is canceled or a task fails.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Gets the first error produced by the group, if any.
    /// </summary>
    public Error? Error => Volatile.Read(ref _error);

    /// <summary>
    /// Executes the supplied delegate and tracks it, propagating failures.
    /// </summary>
    public void Go(Func<CancellationToken, Task<Result<Unit>>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(work));
    }

    /// <summary>
    /// Executes the supplied delegate and tracks it, treating completion as success.
    /// </summary>
    public void Go(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(async ct =>
        {
            await work(ct).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }));
    }

    /// <summary>
    /// Executes the supplied delegate and tracks it, providing a group cancellation token.
    /// </summary>
    public void Go(Func<Task<Result<Unit>>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(_ => work()));
    }

    /// <summary>
    /// Executes the supplied delegate and tracks it, treating completion as success.
    /// </summary>
    public void Go(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(async _ =>
        {
            await work().ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }));
    }

    /// <summary>
    /// Executes the supplied synchronous action and tracks it.
    /// </summary>
    public void Go(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(_ =>
        {
            work();
            return Task.FromResult(Result.Ok(Unit.Value));
        }));
    }

    /// <summary>
    /// Waits for all registered operations to complete and returns the first error, if any.
    /// </summary>
    public async Task<Result<Unit>> WaitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _waitGroup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<Unit>(Error.Canceled(token: oce.CancellationToken.CanBeCanceled ? oce.CancellationToken : cancellationToken));
        }

        var error = Error;
        return error is null
            ? Result.Ok(Unit.Value)
            : Result.Fail<Unit>(error);
    }

    /// <summary>
    /// Cancels the group, notifying all registered delegates.
    /// </summary>
    public void Cancel() => _cts.Cancel();

    private void RegisterAndRun(Func<Task> runner)
    {
        _waitGroup.Add(1);

        try
        {
            Task.Run(async () =>
            {
                try
                {
                    await runner().ConfigureAwait(false);
                }
                finally
                {
                    _waitGroup.Done();
                }
            }, CancellationToken.None);
        }
        catch
        {
            _waitGroup.Done();
            throw;
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task<Result<Unit>>> work)
    {
        try
        {
            var result = await work(Token).ConfigureAwait(false);
            if (result.IsFailure)
            {
                TrySetError(result.Error ?? Error.Unspecified());
            }
        }
        catch (OperationCanceledException oce)
        {
            TrySetError(Error.Canceled(token: oce.CancellationToken.CanBeCanceled ? oce.CancellationToken : null));
        }
        catch (Exception ex)
        {
            TrySetError(Error.FromException(ex));
        }
    }

    private void TrySetError(Error error)
    {
        if (error is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _error, error, null) is null)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The group is being disposed; ignore cancellation during teardown.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Dispose();
    }
}
