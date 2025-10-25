namespace Hugo;

/// <content>
/// Supplies timeout-aware execution helpers.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Creates a timeout result if the operation does not complete within the specified duration.
    /// </summary>
    public static async Task<Result<T>> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        TimeSpan timeout,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        TimeProvider provider = timeProvider ?? TimeProvider.System;

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<T>(Error.Canceled(token: GoExecutionHelpers.ResolveCancellationToken(cancellationToken, oce.CancellationToken)));
            }
            catch (Exception ex)
            {
                return Result.Fail<T>(Error.FromException(ex));
            }
        }

        using CancellationTokenSource operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task<Result<T>> operationTask;
        try
        {
            operationTask = operation(operationCts.Token);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: GoExecutionHelpers.ResolveCancellationToken(cancellationToken, oce.CancellationToken)));
        }
        catch (Exception ex)
        {
            return Result.Fail<T>(Error.FromException(ex));
        }

        Task delayTask = provider.DelayAsync(timeout, delayCts.Token);

        Task completed = await Task.WhenAny(operationTask, delayTask).ConfigureAwait(false);
        if (completed == operationTask)
        {
            await delayCts.CancelAsync().ConfigureAwait(false);

            try
            {
                return await operationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Result.Fail<T>(Error.Canceled(token: GoExecutionHelpers.ResolveCancellationToken(cancellationToken, oce.CancellationToken)));
                }

                return Result.Fail<T>(Error.Canceled(token: GoExecutionHelpers.ResolveCancellationToken(oce.CancellationToken, CancellationToken.None)));
            }
            catch (Exception ex)
            {
                return Result.Fail<T>(Error.FromException(ex));
            }
        }

        if (delayTask.IsCanceled && cancellationToken.IsCancellationRequested)
        {
            await operationCts.CancelAsync().ConfigureAwait(false);

            try
            {
                await operationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<T>(Error.Canceled(token: GoExecutionHelpers.ResolveCancellationToken(cancellationToken, oce.CancellationToken)));
            }
            catch (Exception ex)
            {
                return Result.Fail<T>(Error.FromException(ex));
            }

            return Result.Fail<T>(Error.Canceled(token: GoExecutionHelpers.ResolveCancellationToken(cancellationToken, operationCts.Token)));
        }

        await operationCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            return Result.Fail<T>(Error.FromException(ex));
        }

        return Result.Fail<T>(Error.Timeout(timeout));
    }
}
