using Hugo.Policies;

namespace Hugo.Sagas;

/// <summary>
/// Builds and executes sagas composed of result-based steps with automatic compensation.
/// </summary>
public sealed class ResultSagaBuilder
{
    private readonly List<ResultSagaStep> _steps = [];

    public ResultSagaBuilder AddStep<TState>(
        string name,
        Func<ResultSagaStepContext, CancellationToken, ValueTask<Result<TState>>> operation,
        Func<TState, CancellationToken, ValueTask>? compensation = null,
        string? resultKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);

        var key = string.IsNullOrWhiteSpace(resultKey) ? name : resultKey;
        async ValueTask<Result<object?>> Execute(ResultSagaStepContext context, CancellationToken cancellationToken)
        {
            var result = await operation(context, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                var value = result.Value;
                context.State.Set(key, value);
                if (compensation is not null)
                {
                    context.RegisterCompensation(value, compensation);
                }

                return Result.Ok<object?>(value);
            }

            return Result.Fail<object?>(result.Error);
        }

        _steps.Add(new ResultSagaStep(name, Execute));
        return this;
    }

    /// <summary>
    /// Executes the configured saga, applying the optional execution policy and returning the aggregated saga state.
    /// </summary>
    public Task<Result<ResultSagaState>> ExecuteAsync(
        ResultExecutionPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null) => ExecuteInternalAsync(policy, cancellationToken, timeProvider ?? TimeProvider.System);

    private async Task<Result<ResultSagaState>> ExecuteInternalAsync(
        ResultExecutionPolicy? policy,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (_steps.Count == 0)
        {
            return Result.Fail<ResultSagaState>(Error.From("Saga contains no steps.", ErrorCodes.Validation));
        }

        var effectivePolicy = policy ?? ResultExecutionPolicy.None;
        var sagaState = new ResultSagaState();
        var compensationScope = new CompensationScope();

        foreach (var step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<object?>>>(
                (ctx, token) => step.Execute(new ResultSagaStepContext(ctx, sagaState), token));

            var result = await Result.ExecuteWithPolicyAsync(operation, step.Name, effectivePolicy, timeProvider, cancellationToken).ConfigureAwait(false);
            if (result.Result.IsSuccess)
            {
                compensationScope.Absorb(result.Compensation);
                continue;
            }

            result.Compensation.Clear();
            var failure = result.Result.Error ?? Error.Unspecified();
            var compensationError = await Result.RunCompensationAsync(effectivePolicy, compensationScope, cancellationToken).ConfigureAwait(false);
            if (compensationError is not null)
            {
                failure = Error.Aggregate("Saga execution failed.", failure, compensationError);
            }

            return Result.Fail<ResultSagaState>(failure);
        }

        compensationScope.Clear();
        return Result.Ok(sagaState);
    }

    private sealed class ResultSagaStep(string name, Func<ResultSagaStepContext, CancellationToken, ValueTask<Result<object?>>> executor)
    {
        public string Name { get; } = name;

        public Func<ResultSagaStepContext, CancellationToken, ValueTask<Result<object?>>> Executor { get; } = executor;

        public ValueTask<Result<object?>> Execute(ResultSagaStepContext context, CancellationToken cancellationToken) => Executor(context, cancellationToken);
    }
}
