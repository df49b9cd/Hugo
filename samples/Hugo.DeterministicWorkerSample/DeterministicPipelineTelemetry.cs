using System.Diagnostics;
using System.Diagnostics.Metrics;

using Hugo;

/// <summary>
/// Centralizes ActivitySource and Meter instances used by the deterministic worker sample.
/// </summary>
static class DeterministicPipelineTelemetry
{
    public const string ActivitySourceName = "Hugo.DeterministicWorkerSample";
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

    public static void RecordProcessed(bool isReplay, TimeSpan duration)
    {
        MessagesProcessed.Add(1);
        ProcessingDuration.Record(duration.TotalMilliseconds);

        if (isReplay)
        {
            MessagesReplayed.Add(1);
        }
    }

    public static void RecordFailure()
    {
        MessagesFailed.Add(1);
    }
}
