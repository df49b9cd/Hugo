using Hugo.Policies;

namespace Hugo.Sagas;

/// <summary>
/// Provides saga-specific helpers on top of the pipeline step context.
/// </summary>
public sealed class ResultSagaStepContext
{
    private readonly ResultPipelineStepContext _pipelineContext;
    private readonly ResultSagaState _state;

    internal ResultSagaStepContext(ResultPipelineStepContext pipelineContext, ResultSagaState state)
    {
        _pipelineContext = pipelineContext ?? throw new ArgumentNullException(nameof(pipelineContext));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public string StepName => _pipelineContext.StepName;

    public TimeProvider TimeProvider => _pipelineContext.TimeProvider;

    public ResultSagaState State => _state;

    public void RegisterCompensation(Func<CancellationToken, ValueTask> compensation) => _pipelineContext.RegisterCompensation(compensation);

    public void RegisterCompensation<TState>(TState state, Func<TState, CancellationToken, ValueTask> compensation) => _pipelineContext.RegisterCompensation(state, compensation);

    public bool TryRegisterCompensation<TState>(TState state, Func<TState, CancellationToken, ValueTask> compensation, bool condition) => _pipelineContext.TryRegisterCompensation(state, compensation, condition);
}
