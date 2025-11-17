using System.Diagnostics.CodeAnalysis;

using Hugo.Policies;

using Microsoft.Extensions.Logging;

namespace Hugo;

/// <summary>
/// Provides high-level orchestration helpers for result pipelines, mirroring the Go concurrency surface while preserving compensation scopes.
/// </summary>
public static partial class ResultPipeline
{
    public static ValueTask<Result<IReadOnlyList<T>>> FanOutAsync<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        return Result.WhenAll(operations, policy, timeProvider ?? TimeProvider.System, cancellationToken);
    }

    public static ValueTask<Result<T>> RaceAsync<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        return Result.WhenAny(operations, policy, timeProvider ?? TimeProvider.System, cancellationToken);
    }

    public static async ValueTask<Result<T>> RetryAsync<T>(
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>> operation,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        var provider = timeProvider ?? TimeProvider.System;
        var policy = ResultExecutionBuilders.ExponentialRetryPolicy(
            maxAttempts,
            initialDelay ?? TimeSpan.FromMilliseconds(100));

        int attempts = 0;
        Result<T> finalResult = await Result.RetryWithPolicyAsync(
            async (context, token) =>
            {
                int currentAttempt = Interlocked.Increment(ref attempts);
                Result<T> stepResult = await operation(context, token).ConfigureAwait(false);
                LogAttempt(logger, context.StepName, currentAttempt, maxAttempts, stepResult);
                return stepResult;
            },
            policy,
            provider,
            cancellationToken).ConfigureAwait(false);

        if (finalResult.IsFailure && logger is not null)
        {
            LogPipelineRetryExhausted(logger, maxAttempts, finalResult.Error?.Message ?? Error.Unspecified().Message);
        }

        return finalResult;
    }

    public static ValueTask<Result<T>> WithTimeoutAsync<T>(
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>> operation,
        TimeSpan timeout,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var provider = timeProvider ?? TimeProvider.System;
        return Go.WithTimeoutValueTaskAsync(
            ct => ExecuteSingleAsync(operation, provider, ct),
            timeout,
            provider,
            cancellationToken);
    }

    private static async ValueTask<Result<T>> ExecuteSingleAsync<T>(
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>> operation,
        TimeProvider provider,
        CancellationToken cancellationToken)
    {
        var pipelineResult = await Result.ExecuteWithPolicyAsync(
            operation,
            "pipeline.single",
            ResultExecutionPolicy.None,
            provider,
            cancellationToken).ConfigureAwait(false);

        if (pipelineResult.Result.IsSuccess)
        {
            pipelineResult.Compensation.Clear();
            return pipelineResult.Result;
        }

        var compensationError = await Result.RunCompensationAsync(ResultExecutionPolicy.None, pipelineResult.Compensation, cancellationToken).ConfigureAwait(false);
        if (compensationError is not null)
        {
            var failure = Error.Aggregate(
                "Pipeline operation failed with compensation errors.",
                pipelineResult.Result.Error ?? Error.Unspecified(),
                compensationError);
            return Result.Fail<T>(failure);
        }

        return pipelineResult.Result;
    }

    private static void LogAttempt<T>(ILogger? logger, string stepName, int attempt, int maxAttempts, Result<T> result)
    {
        if (logger is null)
        {
            return;
        }

        if (result.IsSuccess && attempt > 1)
        {
            LogPipelineRetrySucceeded(logger, stepName, attempt, maxAttempts);
            return;
        }

        if (result.IsFailure && attempt < maxAttempts)
        {
            LogPipelineRetryFailed(logger, stepName, attempt, maxAttempts, result.Error?.Message ?? Error.Unspecified().Message);
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Pipeline step {StepName} succeeded on attempt {Attempt}/{MaxAttempts}.")]
    private static partial void LogPipelineRetrySucceeded(ILogger logger, string stepName, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Pipeline step {StepName} failed on attempt {Attempt}/{MaxAttempts}: {Error}")]
    private static partial void LogPipelineRetryFailed(ILogger logger, string stepName, int attempt, int maxAttempts, string error);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "Pipeline retry exhausted {MaxAttempts} attempts. Error: {Error}")]
    private static partial void LogPipelineRetryExhausted(ILogger logger, int maxAttempts, string error);
}
