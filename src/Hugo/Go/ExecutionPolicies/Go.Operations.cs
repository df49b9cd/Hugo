using Hugo.Policies;

namespace Hugo;

/// <content>
/// Provides helpers for orchestrating concurrent operations with policy support.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Executes multiple operations concurrently using <see cref="Result.WhenAll{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, CancellationToken, TimeProvider?)"/> with an optional execution policy.
    /// </summary>
    public static Task<Result<IReadOnlyList<T>>> FanOutAsync<T>(
        IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>(
                    (_, ct) => new ValueTask<Result<T>>(operation(ct))));

        return Result.WhenAll(adapted, policy, cancellationToken, timeProvider);
    }

    /// <summary>
    /// Executes multiple operations concurrently and returns the first successful result via <see cref="Result.WhenAny{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, CancellationToken, TimeProvider?)"/>.
    /// </summary>
    public static Task<Result<T>> RaceAsync<T>(
        IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>(
                    (_, ct) => new ValueTask<Result<T>>(operation(ct))));

        return Result.WhenAny(adapted, policy, cancellationToken, timeProvider);
    }
}
