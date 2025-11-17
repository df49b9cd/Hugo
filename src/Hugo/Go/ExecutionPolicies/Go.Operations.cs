using Hugo.Policies;

namespace Hugo;

/// <content>
/// Provides helpers for orchestrating concurrent operations with policy support.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Executes multiple operations concurrently using <see cref="Result.WhenAll{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, TimeProvider?, CancellationToken)"/> with an optional execution policy.
    /// </summary>
    /// <typeparam name="T">The result type produced by each operation.</typeparam>
    /// <param name="operations">The operations to execute concurrently.</param>
    /// <param name="policy">The optional execution policy applied to each operation.</param>
    /// <param name="timeProvider">The optional time provider used by the execution policy.</param>
    /// <param name="cancellationToken">The token used to cancel the aggregate operation.</param>
    /// <returns>A result containing the collection of operation outputs.</returns>
    public static ValueTask<Result<IReadOnlyList<T>>> FanOutAsync<T>(
        IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<CancellationToken, ValueTask<Result<T>>>(ct => new ValueTask<Result<T>>(operation(ct))));

        return FanOutValueTaskAsync(adapted, policy, timeProvider, cancellationToken);
    }

    /// <summary>
    /// Executes multiple operations concurrently using <see cref="Result.WhenAll{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, TimeProvider?, CancellationToken)"/> with an optional execution policy.
    /// </summary>
    /// <typeparam name="T">The result type produced by each operation.</typeparam>
    /// <param name="operations">The operations to execute concurrently.</param>
    /// <param name="policy">The optional execution policy applied to each operation.</param>
    /// <param name="timeProvider">The optional time provider used by the execution policy.</param>
    /// <param name="cancellationToken">The token used to cancel the aggregate operation.</param>
    /// <returns>A result containing the collection of operation outputs.</returns>
    public static async ValueTask<Result<IReadOnlyList<T>>> FanOutValueTaskAsync<T>(
        IEnumerable<Func<CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>(
                    (_, ct) => operation(ct)));

        return await Result.WhenAll(adapted, policy, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes multiple operations concurrently and returns the first successful result via <see cref="Result.WhenAny{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, TimeProvider?, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="T">The result type produced by each operation.</typeparam>
    /// <param name="operations">The operations to execute concurrently.</param>
    /// <param name="policy">The optional execution policy applied to each operation.</param>
    /// <param name="timeProvider">The optional time provider used by the execution policy.</param>
    /// <param name="cancellationToken">The token used to cancel the aggregate operation.</param>
    /// <returns>A result containing the first successful operation output.</returns>
    public static ValueTask<Result<T>> RaceAsync<T>(
        IEnumerable<Func<CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>(
                    (_, ct) => operation(ct)));

        return Result.WhenAny(adapted, policy, timeProvider, cancellationToken);
    }
}
