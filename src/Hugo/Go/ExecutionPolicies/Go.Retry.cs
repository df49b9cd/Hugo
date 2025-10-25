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
    public static async Task<Result<T>> RetryAsync<T>(
        Func<int, CancellationToken, Task<Result<T>>> operation,
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

                        if (result.IsSuccess && currentAttempt > 1)
                        {
                            logger?.LogInformation(
                                "Operation succeeded on attempt {Attempt} of {MaxAttempts}",
                                currentAttempt,
                                maxAttempts);
                        }
                        else if (result.IsFailure && currentAttempt < maxAttempts)
                        {
                            logger?.LogWarning(
                                "Operation failed on attempt {Attempt} of {MaxAttempts}: {Error}",
                                currentAttempt,
                                maxAttempts,
                                result.Error?.Message);
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
                        logger?.LogWarning(
                            ex,
                            "Operation threw on attempt {Attempt} of {MaxAttempts}",
                            currentAttempt,
                            maxAttempts);
                        return Result.Fail<T>(Error.FromException(ex));
                    }
                },
                policy,
                cancellationToken,
                provider)
            .ConfigureAwait(false);

        if (finalResult.IsFailure)
        {
            logger?.LogError(
                "Operation failed after {MaxAttempts} attempts: {Error}",
                maxAttempts,
                finalResult.Error?.Message);
        }

        return finalResult;
    }
}
