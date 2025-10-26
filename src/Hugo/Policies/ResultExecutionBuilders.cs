using Hugo.Sagas;

namespace Hugo.Policies;

/// <summary>
/// Provides fluent helpers for common retry patterns involving <see cref="ErrGroup"/> and <see cref="Result{T}"/> pipelines.
/// </summary>
public static class ResultExecutionBuilders
{
    /// <summary>
    /// Creates a fixed delay retry policy that treats the first attempt as attempt number one.
    /// </summary>
    /// <remarks>
    /// This helper compensates for the fact that <see cref="ResultRetryPolicy.FixedDelay"/> expects <paramref name="attempts"/> to include the initial invocation.
    /// </remarks>
    /// <param name="attempts">The total number of attempts, including the initial execution.</param>
    /// <param name="delay">The delay applied between attempts.</param>
    /// <param name="compensation">The optional compensation policy to apply on failure.</param>
    /// <returns>A composed execution policy containing the retry configuration.</returns>
    public static ResultExecutionPolicy FixedRetryPolicy(int attempts, TimeSpan delay, ResultCompensationPolicy? compensation = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var retry = ResultRetryPolicy.FixedDelay(attempts, delay);
        var policy = ResultExecutionPolicy.None.WithRetry(retry);
        return compensation is null ? policy : policy.WithCompensation(compensation);
    }

    /// <summary>
    /// Creates an exponential backoff policy with optional compensation behaviour.
    /// </summary>
    /// <param name="attempts">The total number of attempts, including the initial execution.</param>
    /// <param name="initialDelay">The delay applied before the second attempt.</param>
    /// <param name="multiplier">The growth factor applied between subsequent attempts.</param>
    /// <param name="maxDelay">The optional upper bound for the calculated delay.</param>
    /// <param name="compensation">The optional compensation policy to apply on failure.</param>
    /// <returns>A composed execution policy containing the retry configuration.</returns>
    public static ResultExecutionPolicy ExponentialRetryPolicy(int attempts, TimeSpan initialDelay, double multiplier = 2.0, TimeSpan? maxDelay = null, ResultCompensationPolicy? compensation = null)
    {
        var retry = ResultRetryPolicy.Exponential(attempts, initialDelay, multiplier, maxDelay);
        var policy = ResultExecutionPolicy.None.WithRetry(retry);
        return compensation is null ? policy : policy.WithCompensation(compensation);
    }

    /// <summary>
    /// Builds a saga over the supplied steps and executes it under the provided policy.
    /// </summary>
    /// <param name="policy">The execution policy that governs retries and compensation.</param>
    /// <param name="configure">Delegates that configure the saga builder.</param>
    /// <returns>A saga builder populated by the supplied configuration actions.</returns>
    public static ResultSagaBuilder CreateSaga(ResultExecutionPolicy policy, params Action<ResultSagaBuilder>[] configure)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ResultSagaBuilder();
        foreach (var action in configure)
        {
            action(builder);
        }

        return builder;
    }
}
