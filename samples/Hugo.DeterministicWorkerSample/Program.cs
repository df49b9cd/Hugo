using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Channels;
using System.Text.Json;

using Hugo;
using Hugo.Sagas;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLogging(logging =>
{
    logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = false;
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDeterministicStateStore, InMemoryDeterministicStateStore>();
builder.Services.AddSingleton(sp =>
{
    IDeterministicStateStore store = sp.GetRequiredService<IDeterministicStateStore>();
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    JsonSerializerOptions options = CreateSampleSerializerOptions();
    return new VersionGate(store, timeProvider, options);
});
builder.Services.AddSingleton(sp =>
{
    IDeterministicStateStore store = sp.GetRequiredService<IDeterministicStateStore>();
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    JsonSerializerOptions options = CreateSampleSerializerOptions();
    return new DeterministicEffectStore(store, timeProvider, options);
});
builder.Services.AddSingleton<DeterministicGate>();
builder.Services.AddSingleton<SimulatedKafkaTopic>();
builder.Services.AddSingleton<PipelineEntityStore>();
builder.Services.AddSingleton<PipelineSaga>();
builder.Services.AddSingleton<DeterministicPipelineProcessor>();
builder.Services.AddHostedService<KafkaWorker>();
builder.Services.AddHostedService<SampleScenario>();

IHost app = builder.Build();
await app.RunAsync();
return;

static JsonSerializerOptions CreateSampleSerializerOptions()
{
    JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
    options.TypeInfoResolverChain.Add(DeterministicPipelineSerializerContext.Default);
    return options;
}

sealed class KafkaWorker(
    SimulatedKafkaTopic topic,
    DeterministicPipelineProcessor processor,
    ILogger<KafkaWorker> logger) : BackgroundService
{
    private readonly SimulatedKafkaTopic _topic = topic ?? throw new ArgumentNullException(nameof(topic));
    private readonly DeterministicPipelineProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly ILogger<KafkaWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ChannelReader<SimulatedKafkaMessage> reader = _topic.Reader;

        await foreach (SimulatedKafkaMessage message in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            Result<ProcessingOutcome> result = await _processor.ProcessAsync(message, stoppingToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                Error error = result.Error ?? Error.Unspecified();
                _logger.LogError(
                    "Failed to process message {MessageId} for {EntityId}: {ErrorCode} - {ErrorMessage}",
                    message.MessageId,
                    message.EntityId,
                    error.Code ?? ErrorCodes.Unspecified,
                    error.Message);
                continue;
            }

            ProcessingOutcome outcome = result.Value;
            if (outcome.IsReplay)
            {
                _logger.LogInformation(
                    "Replayed {MessageId} (version {Version}) for {EntityId}: total={Total:F2} average={Average:F2} processed={Processed}",
                    message.MessageId,
                    outcome.Version,
                    outcome.Entity.EntityId,
                    outcome.Entity.RunningTotal,
                    outcome.Entity.RunningAverage,
                    outcome.Entity.ProcessedCount);
            }
            else
            {
                _logger.LogInformation(
                    "Processed {MessageId} for {EntityId}: total={Total:F2} average={Average:F2} processed={Processed}",
                    message.MessageId,
                    outcome.Entity.EntityId,
                    outcome.Entity.RunningTotal,
                    outcome.Entity.RunningAverage,
                    outcome.Entity.ProcessedCount);
            }
        }
    }
}

sealed class SampleScenario(
    SimulatedKafkaTopic topic,
    PipelineEntityStore store,
    TimeProvider timeProvider,
    ILogger<SampleScenario> logger) : BackgroundService
{
    private readonly SimulatedKafkaTopic _topic = topic ?? throw new ArgumentNullException(nameof(topic));
    private readonly PipelineEntityStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<SampleScenario> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);

            DateTimeOffset now = _timeProvider.GetUtcNow();
            SimulatedKafkaMessage first = new("msg-001", "trend-042", 3.40, now);
            SimulatedKafkaMessage second = new("msg-002", "trend-042", 2.25, now.AddSeconds(1));
            SimulatedKafkaMessage third = new("msg-003", "trend-107", 6.80, now.AddSeconds(2));
            SimulatedKafkaMessage replay = first with { ObservedAt = now.AddSeconds(5) };

            SimulatedKafkaMessage[] script =
            [
                first,
                second,
                third,
                replay
            ];

            foreach (SimulatedKafkaMessage message in script)
            {
                await _topic.PublishAsync(message, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Published {MessageId} -> {EntityId} ({Amount:F2}) at {ObservedAt:o}",
                    message.MessageId,
                    message.EntityId,
                    message.Amount,
                    message.ObservedAt);
                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

            IReadOnlyList<PipelineEntity> snapshot = _store.Snapshot();
            foreach (PipelineEntity entity in snapshot)
            {
                _logger.LogInformation(
                    "Store snapshot {EntityId}: total={Total:F2} average={Average:F2} processed={Processed}",
                    entity.EntityId,
                    entity.RunningTotal,
                    entity.RunningAverage,
                    entity.ProcessedCount);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host is shutting down
        }
    }
}

sealed class DeterministicPipelineProcessor(
    DeterministicGate gate,
    PipelineSaga saga,
    ILogger<DeterministicPipelineProcessor> logger)
{
    private readonly DeterministicGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly PipelineSaga _saga = saga ?? throw new ArgumentNullException(nameof(saga));
    private readonly ILogger<DeterministicPipelineProcessor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task<Result<ProcessingOutcome>> ProcessAsync(SimulatedKafkaMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        string changeId = $"deterministic-pipeline::{message.MessageId}";
        const int Version = 1;

        return _gate
            .Workflow<ProcessingOutcome>(changeId, Version, Version)
            .ForVersion(
                Version,
                async (context, ct) =>
                {
                    Result<PipelineEntity> sagaResult = await _saga.ExecuteAsync(message, context, ct).ConfigureAwait(false);
                    if (sagaResult.IsFailure)
                    {
                        Error error = sagaResult.Error ?? Error.Unspecified();
                        _logger.LogWarning(
                            "Saga failed for {MessageId}: {Error}",
                            message.MessageId,
                            error.Message);
                        return Result.Fail<ProcessingOutcome>(error);
                    }

                    PipelineEntity entity = sagaResult.Value;
                    return Result.Ok(new ProcessingOutcome(!context.IsNew, context.Version, entity));
                })
            .ExecuteAsync(cancellationToken);
    }
}

sealed class PipelineSaga(
    PipelineEntityStore store,
    ILogger<PipelineSaga> logger)
{
    private readonly PipelineEntityStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<PipelineSaga> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private static class SagaKeys
    {
        public const string Entity = "entity";
        public const string Computation = "computation";
        public const string Updated = "updated";
        public const string Persisted = "persisted";
        public const string PersistScope = "entity-save";
    }

    public async Task<Result<PipelineEntity>> ExecuteAsync(
        SimulatedKafkaMessage message,
        DeterministicGate.DeterministicWorkflowContext workflowContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(workflowContext);

        var builder = new ResultSagaBuilder();

        builder.AddStep(
            "load-entity",
            (stepContext, token) => _store.LoadAsync(message.EntityId, token),
            resultKey: SagaKeys.Entity);

        builder.AddStep(
            "calculate-next-state",
            (stepContext, _) =>
            {
                if (!stepContext.State.TryGet<PipelineEntity>(SagaKeys.Entity, out PipelineEntity entity))
                {
                    return ValueTask.FromResult(Result.Fail<PipelineComputation>(MissingSagaState(SagaKeys.Entity)));
                }

                PipelineComputation computation = PipelineComputation.Create(entity, message);
                return ValueTask.FromResult(Result.Ok(computation));
            },
            resultKey: SagaKeys.Computation);

        builder.AddStep(
            "apply-mutation",
            (stepContext, _) =>
            {
                if (!stepContext.State.TryGet<PipelineEntity>(SagaKeys.Entity, out PipelineEntity entity))
                {
                    return ValueTask.FromResult(Result.Fail<PipelineEntity>(MissingSagaState(SagaKeys.Entity)));
                }

                if (!stepContext.State.TryGet<PipelineComputation>(SagaKeys.Computation, out PipelineComputation computation))
                {
                    return ValueTask.FromResult(Result.Fail<PipelineEntity>(MissingSagaState(SagaKeys.Computation)));
                }

                PipelineEntity updated = computation.ApplyTo(entity, message);
                return ValueTask.FromResult(Result.Ok(updated));
            },
            resultKey: SagaKeys.Updated);

        builder.AddStep(
            "persist-entity",
            async (stepContext, token) =>
            {
                if (!stepContext.State.TryGet<PipelineEntity>(SagaKeys.Updated, out PipelineEntity updated))
                {
                    return Result.Fail<PipelineEntity>(MissingSagaState(SagaKeys.Updated));
                }

                Result<PipelineEntity> persisted = await workflowContext
                    .CaptureAsync(
                        SagaKeys.PersistScope,
                        async ct => await _store.SaveAsync(updated, ct).ConfigureAwait(false),
                        token)
                    .ConfigureAwait(false);

                if (persisted.IsSuccess)
                {
                    _logger.LogDebug(
                        "Persisted entity {EntityId} after message {MessageId} (version {Version})",
                        updated.EntityId,
                        message.MessageId,
                        workflowContext.Version);
                }

                return persisted;
            },
            resultKey: SagaKeys.Persisted);

        Result<ResultSagaState> sagaResult = await builder.ExecuteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (sagaResult.IsFailure)
        {
            return Result.Fail<PipelineEntity>(sagaResult.Error);
        }

        ResultSagaState state = sagaResult.Value;
        if (!state.TryGet<PipelineEntity>(SagaKeys.Persisted, out PipelineEntity entity))
        {
            return Result.Fail<PipelineEntity>(MissingSagaState(SagaKeys.Persisted));
        }

        return Result.Ok(entity);
    }

    private static Error MissingSagaState(string key) =>
        Error.From($"Saga state is missing '{key}'.", ErrorCodes.Validation);
}

sealed class PipelineEntityStore
{
    private readonly ConcurrentDictionary<string, PipelineEntity> _entities = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<Result<PipelineEntity>> LoadAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        cancellationToken.ThrowIfCancellationRequested();

        if (_entities.TryGetValue(entityId, out PipelineEntity? entity))
        {
            PipelineEntity existing = entity ?? PipelineEntity.Create(entityId);
            return ValueTask.FromResult(Result.Ok(existing));
        }

        return ValueTask.FromResult(Result.Ok(PipelineEntity.Create(entityId)));
    }

    public ValueTask<Result<PipelineEntity>> SaveAsync(PipelineEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();

        _entities[entity.EntityId] = entity;
        return ValueTask.FromResult(Result.Ok(entity));
    }

    public IReadOnlyList<PipelineEntity> Snapshot() =>
        _entities.Values
            .OrderBy(static e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

sealed record class PipelineEntity(
    string EntityId,
    double RunningTotal,
    int ProcessedCount,
    double LastAmount,
    DateTimeOffset LastUpdated)
{
    public double RunningAverage => ProcessedCount == 0 ? 0d : RunningTotal / ProcessedCount;

    public static PipelineEntity Create(string entityId) =>
        new(entityId, 0d, 0, 0d, DateTimeOffset.MinValue);
}

readonly record struct PipelineComputation(double Total, int ProcessedCount)
{
    public double Average => ProcessedCount == 0 ? 0d : Total / ProcessedCount;

    public static PipelineComputation Create(PipelineEntity entity, SimulatedKafkaMessage message)
    {
        double total = entity.RunningTotal + message.Amount;
        int processed = entity.ProcessedCount + 1;
        return new PipelineComputation(total, processed);
    }

    public PipelineEntity ApplyTo(PipelineEntity entity, SimulatedKafkaMessage message) =>
        new(
            entity.EntityId,
            Total,
            ProcessedCount,
            message.Amount,
            message.ObservedAt);
}

readonly record struct ProcessingOutcome(bool IsReplay, int Version, PipelineEntity Entity);

sealed record class SimulatedKafkaMessage(string MessageId, string EntityId, double Amount, DateTimeOffset ObservedAt)
{
    public static SimulatedKafkaMessage Create(string entityId, double amount, DateTimeOffset observedAt) =>
        new($"msg-{Guid.NewGuid():N}", entityId, amount, observedAt);
}

sealed class SimulatedKafkaTopic
{
    private readonly Channel<SimulatedKafkaMessage> _channel = Channel.CreateUnbounded<SimulatedKafkaMessage>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false
    });

    public ChannelReader<SimulatedKafkaMessage> Reader => _channel.Reader;

    public ValueTask PublishAsync(SimulatedKafkaMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        return _channel.Writer.WriteAsync(message, cancellationToken);
    }
}
