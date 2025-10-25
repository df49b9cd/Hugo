using System.Threading.Channels;

namespace Hugo;

/// <content>
/// Provides channel factory helpers and lightweight primitives.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Creates a fluent builder that configures a bounded channel instance.
    /// </summary>
    public static BoundedChannelBuilder<T> BoundedChannel<T>(int capacity) => new(capacity);

    /// <summary>
    /// Creates a fluent builder that configures a prioritized channel instance.
    /// </summary>
    public static PrioritizedChannelBuilder<T> PrioritizedChannel<T>() => new();

    /// <summary>
    /// Creates a fluent builder that configures a prioritized channel instance with the specified priority levels.
    /// </summary>
    public static PrioritizedChannelBuilder<T> PrioritizedChannel<T>(int priorityLevels) => new(priorityLevels);

    public static Channel<T> MakeChannel<T>(
        int? capacity = null,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait,
        bool singleReader = false,
        bool singleWriter = false)
    {
        if (capacity is > 0)
        {
            BoundedChannelOptions options = new(capacity.Value)
            {
                FullMode = fullMode,
                SingleReader = singleReader,
                SingleWriter = singleWriter
            };

            return Channel.CreateBounded<T>(options);
        }

        UnboundedChannelOptions unboundedOptions = new()
        {
            SingleReader = singleReader,
            SingleWriter = singleWriter
        };

        return Channel.CreateUnbounded<T>(unboundedOptions);
    }

    public static Channel<T> MakeChannel<T>(BoundedChannelOptions options) =>
        options is null ? throw new ArgumentNullException(nameof(options)) : Channel.CreateBounded<T>(options);

    public static Channel<T> MakeChannel<T>(UnboundedChannelOptions options) =>
        options is null ? throw new ArgumentNullException(nameof(options)) : Channel.CreateUnbounded<T>(options);

    public static PrioritizedChannel<T> MakeChannel<T>(PrioritizedChannelOptions options) =>
        options is null ? throw new ArgumentNullException(nameof(options)) : new PrioritizedChannel<T>(options);

    public static PrioritizedChannel<T> MakePrioritizedChannel<T>(
        int priorityLevels,
        int? capacityPerLevel = null,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait,
        bool singleReader = false,
        bool singleWriter = false,
        int? defaultPriority = null)
    {
        PrioritizedChannelOptions options = new()
        {
            PriorityLevels = priorityLevels,
            CapacityPerLevel = capacityPerLevel,
            FullMode = fullMode,
            SingleReader = singleReader,
            SingleWriter = singleWriter
        };

        if (defaultPriority.HasValue)
        {
            options.DefaultPriority = defaultPriority.Value;
        }

        return MakeChannel<T>(options);
    }

    public readonly record struct Unit
    {
        public static readonly Unit Value = new();
    }
}
