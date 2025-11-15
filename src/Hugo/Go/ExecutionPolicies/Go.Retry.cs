using System.Threading.Tasks;

using Hugo.Policies;

using Microsoft.Extensions.Logging;

namespace Hugo;

/// <content>
/// Implements retry helpers built on top of the result execution pipeline.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Retries an operation with exponential backoff using <see cref="Result.RetryWithPolicyAsync{T}(Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}, ResultExecutionPolicy, CancellationToken, TimeProvider?)"/>.
    /// </summary>
    /// <typeparam name="T">The result type produced by the operation.</typeparam>
    /// <param name="operation">The operation to execute with retry semantics.</param>
    /// <param name="maxAttempts">The maximum number of attempts to perform.</param>
    /// <param name="initialDelay">The initial delay between attempts; subsequent delays grow exponentially.</param>
    /// <param name="timeProvider">The optional time provider used for delay calculations.</param>
    /// <param name="logger">The optional logger that receives retry telemetry.</param>
    /// <param name="cancellationToken">The token used to cancel the retries.</param>
    /// <returns>A result containing the final operation outcome.</returns>
    public static ValueTask<Result<T>> RetryAsync<T>(
        Func<int, CancellationToken, Task<Result<T>>> operation,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RetryValueTaskAsync(
            (attempt, ct) => new ValueTask<Result<T>>(operation(attempt, ct)),
            maxAttempts,
            initialDelay,
            timeProvider,
            logger,
            cancellationToken);
    }

    /// <summary>
    /// Retries an operation with exponential backoff using <see cref="Result.RetryWithPolicyAsync{T}(Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}, ResultExecutionPolicy, CancellationToken, TimeProvider?)"/>.
    /// </summary>
    /// <typeparam name="T">The result type produced by the operation.</typeparam>
    /// <param name="operation">The operation to execute with retry semantics.</param>
    /// <param name="maxAttempts">The maximum number of attempts to perform.</param>
    /// <param name="initialDelay">The initial delay between attempts; subsequent delays grow exponentially.</param>
    /// <param name="timeProvider">The optional time provider used for delay calculations.</param>
    /// <param name="logger">The optional logger that receives retry telemetry.</param>
    /// <param name="cancellationToken">The token used to cancel the retries.</param>
    /// <returns>A result containing the final operation outcome.</returns>
    public static async ValueTask<Result<T>> RetryValueTaskAsync<T>(
        Func<int, CancellationToken, ValueTask<Result<T>>> operation,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        TimeProvider provider = timeProvider ?? TimeProvider.System;
        ResultExecutionPolicy policy = ResultExecutionBuilders.ExponentialRetryPolicy(
            maxAttempts,
            initialDelay ?? TimeSpan.FromMilliseconds(100));

        int attempt = 0;

        Result<T> finalResult = await Result.RetryWithPolicyAsync(
                async (_, ct) =>
                {
                    int currentAttempt = Interlocked.Increment(ref attempt);

                    try
                    {
                        Result<T> result = await operation(currentAttempt, ct).ConfigureAwait(false);

                        if (logger is not null)
                        {
                            if (result.IsSuccess && currentAttempt > 1)
                            {
                                LogRetrySuccess(logger, currentAttempt, maxAttempts);
                            }
                            else if (result.IsFailure && currentAttempt < maxAttempts)
                            {
                                LogRetryFailure(logger, currentAttempt, maxAttempts, result.Error?.Message ?? Error.Unspecified().Message);
                            }
                        }

                        return result;
                    }
                    catch (OperationCanceledException oce)
                    {
                        CancellationToken token = oce.CancellationToken;
                        if (!token.CanBeCanceled && ct.CanBeCanceled)
                        {
                            token = ct;
                        }

                        return Result.Fail<T>(Error.Canceled(token: token.CanBeCanceled ? token : null));
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            LogRetryException(logger, ex, currentAttempt, maxAttempts);
                        }

                        return Result.Fail<T>(Error.FromException(ex));
                    }
                },
                policy,
                provider,
                cancellationToken)
            .ConfigureAwait(false);

        if (finalResult.IsFailure)
        {
            if (logger is not null)
            {
                LogRetryFinalFailure(logger, maxAttempts, finalResult.Error?.Message ?? Error.Unspecified().Message);
            }
        }

        return finalResult;
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Operation succeeded on attempt {Attempt} of {MaxAttempts}")]
    private static partial void LogRetrySuccess(ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Operation failed on attempt {Attempt} of {MaxAttempts}: {Error}")]
    private static partial void LogRetryFailure(ILogger logger, int attempt, int maxAttempts, string error);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Operation threw on attempt {Attempt} of {MaxAttempts}")]
    private static partial void LogRetryException(ILogger logger, Exception exception, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "Operation failed after {MaxAttempts} attempts: {Error}")]
    private static partial void LogRetryFinalFailure(ILogger logger, int maxAttempts, string error);
}
