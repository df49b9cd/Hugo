namespace Hugo;

using Hugo.Policies;

using Unit = Hugo.Go.Unit;

/// <summary>
/// Coordinates asynchronous operations, propagating the first failure and cancelling remaining work, similar to Go's errgroup package.
/// </summary>
/// <remarks>
/// Reusing an <see cref="ErrGroup"/> after <see cref="Dispose"/> is unsupported and results in an <see cref="ObjectDisposedException"/> from any <c>Go(...)</c> overload.
/// </remarks>
/// <param name="cancellationToken">An optional cancellation token that cancels the group when signaled.</param>
public sealed class ErrGroup(CancellationToken cancellationToken = default) : IDisposable
{
    private readonly CancellationTokenSource _cts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
    private readonly CancellationToken _linkedToken = cancellationToken;
    private readonly WaitGroup _waitGroup = new();
    private Error? _error;
    private int _disposed;

    /// <summary>
    /// Gets a cancellation token that is signaled when the group is canceled or a task fails; remains usable even after the group is disposed.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Gets the first error produced by the group, if any.
    /// </summary>
    public Error? Error => Volatile.Read(ref _error);

    /// <summary>Executes the supplied delegate and tracks it, propagating failures.</summary>
    /// <param name="work">The delegate to execute.</param>
    public void Go(Func<CancellationToken, Task<Result<Unit>>> work)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(work));
    }

    /// <summary>Executes the supplied delegate and tracks it, treating completion as success.</summary>
    /// <param name="work">The delegate to execute.</param>
    public void Go(Func<CancellationToken, Task> work)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(async ct =>
        {
            await work(ct).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }));
    }

    /// <summary>Executes the supplied delegate and tracks it, providing a group cancellation token.</summary>
    /// <param name="work">The delegate to execute.</param>
    public void Go(Func<Task<Result<Unit>>> work)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(_ => work()));
    }

    /// <summary>Executes the supplied delegate and tracks it, treating completion as success.</summary>
    /// <param name="work">The delegate to execute.</param>
    public void Go(Func<Task> work)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(async _ =>
        {
            await work().ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }));
    }

    /// <summary>Executes the supplied synchronous action and tracks it.</summary>
    /// <param name="work">The action to execute.</param>
    public void Go(Action work)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(work);

        RegisterAndRun(() => ExecuteAsync(_ =>
        {
            work();
            return Task.FromResult(Result.Ok(Unit.Value));
        }));
    }

    /// <summary>
    /// Executes a result pipeline-aware delegate with retry/compensation support.
    /// </summary>
    /// <param name="work">Delegate that participates in the result pipeline.</param>
    /// <param name="stepName">Optional name used for diagnostics.</param>
    /// <param name="policy">Optional execution policy controlling retries and compensation.</param>
    /// <param name="timeProvider">Optional time provider used for retry scheduling.</param>
    public void Go(
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<Unit>>> work,
        string? stepName = null,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(work);

        var effectivePolicy = policy ?? ResultExecutionPolicy.None;
        var resolvedName = string.IsNullOrWhiteSpace(stepName) ? "errgroup.step" : stepName;
        var provider = timeProvider ?? TimeProvider.System;

        RegisterAndRun(() => ExecutePipelineAsync(resolvedName, work, effectivePolicy, provider));
    }

    /// <summary>Waits for all registered operations to complete and returns the first error, if any; manual cancellation results in a failed outcome with <see cref="ErrorCodes.Canceled"/>.</summary>
    /// <param name="cancellationToken">The token used to cancel the wait.</param>
    /// <returns>A result describing the group outcome.</returns>
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

    /// <summary>Cancels the group, notifying all registered delegates.</summary>
    /// <remarks>Manual cancellation causes <see cref="WaitAsync(CancellationToken)"/> to return a failed <see cref="Result{Unit}"/> with <see cref="ErrorCodes.Canceled"/>.</remarks>
    public void Cancel()
    {
        if (Volatile.Read(ref _error) is null)
        {
            var cancellationError = Error.Canceled("ErrGroup was canceled.", Token);
            TrySetError(cancellationError);
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                throw;
            }
        }
    }

    private void RegisterAndRun(Func<Task> runner)
    {
        _waitGroup.Add(1);

        try
        {
            ThrowIfDisposed();
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

    private async Task ExecutePipelineAsync(
        string stepName,
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<Unit>>> work,
        ResultExecutionPolicy policy,
        TimeProvider timeProvider)
    {
        try
        {
            var result = await Result.ExecuteWithPolicyAsync(work, stepName, policy, timeProvider, Token).ConfigureAwait(false);
            if (result.Result.IsSuccess)
            {
                result.Compensation.Clear();
                return;
            }

            var compensationScope = result.Compensation;
            var failure = result.Result.Error ?? Error.Unspecified();
            var ownsError = TrySetError(failure);
            var compensationError = await Result.RunCompensationAsync(policy, compensationScope, _linkedToken).ConfigureAwait(false);
            compensationScope.Clear();
            if (compensationError is not null)
            {
                var aggregated = Error.Aggregate("ErrGroup step failed with compensation error.", failure, compensationError);
                if (ownsError)
                {
                    UpdateError(failure, aggregated);
                }
                else
                {
                    TrySetError(aggregated);
                }
            }
        }
        catch (OperationCanceledException oce)
        {
            TrySetError(Error.Canceled(token: oce.CancellationToken.CanBeCanceled ? oce.CancellationToken : Token));
        }
        catch (Exception ex)
        {
            TrySetError(Error.FromException(ex));
        }
    }

    private bool TrySetError(Error error)
    {
        if (error is null)
        {
            return false;
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

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void UpdateError(Error expected, Error replacement)
    {
        if (expected is null || replacement is null || ReferenceEquals(expected, replacement))
        {
            return;
        }

        Interlocked.CompareExchange(ref _error, replacement, expected);
    }
}
