using System.Diagnostics;
using System.Diagnostics.Metrics;

using Hugo;

/// <summary>
/// Centralizes ActivitySource and Meter instances used by the deterministic worker sample.
/// </summary>
static class DeterministicPipelineTelemetry
{
    /// <summary>
    /// The activity source name emitted by the deterministic worker sample.
    /// </summary>
    public const string ActivitySourceName = "Hugo.DeterministicWorkerSample";

    /// <summary>
    /// The meter name emitted by the deterministic worker sample.
    /// </summary>
    public const string MeterName = "Hugo.DeterministicWorkerSample";
    private const string Version = "1.0.0";

    private static readonly ActivitySource ActivitySource = GoDiagnostics.CreateActivitySource(ActivitySourceName, Version, schemaUrl: (Uri?)null);
    private static readonly Meter Meter = new(MeterName, Version);

    private static readonly Counter<int> MessagesProcessed = Meter.CreateCounter<int>("sample.pipeline.messages.processed");
    private static readonly Counter<int> MessagesReplayed = Meter.CreateCounter<int>("sample.pipeline.messages.replayed");
    private static readonly Counter<int> MessagesFailed = Meter.CreateCounter<int>("sample.pipeline.messages.failed");
    private static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(
        "sample.pipeline.processing.duration",
        unit: "ms",
        description: "Time the saga spends processing each message.");

    static DeterministicPipelineTelemetry()
    {
        GoDiagnostics.Configure(Meter, ActivitySource);
    }

    /// <summary>
    /// Starts a consumer activity that tracks processing of the supplied message.
    /// </summary>
    /// <param name="message">The simulated Kafka message being processed.</param>
    /// <returns>The activity representing the processing scope, or <c>null</c> when instrumentation is disabled.</returns>
    public static Activity? StartProcessingActivity(SimulatedKafkaMessage message)
    {
        Activity? activity = ActivitySource.StartActivity("ProcessMessage", ActivityKind.Consumer);
        if (activity is not null)
        {
            activity.SetTag("messaging.system", "sample.kafka");
            activity.SetTag("messaging.operation", "process");
            activity.SetTag("messaging.message_id", message.MessageId);
            activity.SetTag("messaging.destination", "deterministic-topic");
            activity.SetTag("sample.entity_id", message.EntityId);
        }

        return activity;
    }

    /// <summary>
    /// Records successful processing metrics and tracks whether the outcome was replayed.
    /// </summary>
    /// <param name="isReplay">Indicates whether the deterministic gate replayed a prior result.</param>
    /// <param name="duration">The time spent processing the message.</param>
    public static void RecordProcessed(bool isReplay, TimeSpan duration)
    {
        MessagesProcessed.Add(1);
        ProcessingDuration.Record(duration.TotalMilliseconds);

        if (isReplay)
        {
            MessagesReplayed.Add(1);
        }
    }

    /// <summary>
    /// Records a failed processing attempt for the sample pipeline.
    /// </summary>
    public static void RecordFailure()
    {
        MessagesFailed.Add(1);
    }
}
