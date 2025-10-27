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
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="capacity">The maximum number of items buffered by the channel.</param>
    /// <returns>A builder that can configure and create the channel.</returns>
    public static BoundedChannelBuilder<T> BoundedChannel<T>(int capacity) => new(capacity);

    /// <summary>
    /// Creates a fluent builder that configures a prioritized channel instance.
    /// </summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <returns>A builder that can configure and create the channel.</returns>
    public static PrioritizedChannelBuilder<T> PrioritizedChannel<T>() => new();

    /// <summary>
    /// Creates a fluent builder that configures a prioritized channel instance with the specified priority levels.
    /// </summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="priorityLevels">The number of priority queues to maintain.</param>
    /// <returns>A builder that can configure and create the channel.</returns>
    public static PrioritizedChannelBuilder<T> PrioritizedChannel<T>(int priorityLevels) => new(priorityLevels);

    /// <summary>
    /// Creates a channel using either bounded or unbounded options based on the provided arguments.
    /// </summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="capacity">The optional capacity to create a bounded channel; <see langword="null"/> yields an unbounded channel.</param>
    /// <param name="fullMode">The strategy used when the channel is full.</param>
    /// <param name="singleReader"><see langword="true"/> to optimize for a single reader; otherwise <see langword="false"/>.</param>
    /// <param name="singleWriter"><see langword="true"/> to optimize for a single writer; otherwise <see langword="false"/>.</param>
    /// <returns>A configured channel instance.</returns>
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

    /// <summary>Creates a bounded channel using the supplied options.</summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="options">The bounded channel configuration.</param>
    /// <returns>A configured channel instance.</returns>
    public static Channel<T> MakeChannel<T>(BoundedChannelOptions options) =>
        options is null ? throw new ArgumentNullException(nameof(options)) : Channel.CreateBounded<T>(options);

    /// <summary>Creates an unbounded channel using the supplied options.</summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="options">The unbounded channel configuration.</param>
    /// <returns>A configured channel instance.</returns>
    public static Channel<T> MakeChannel<T>(UnboundedChannelOptions options) =>
        options is null ? throw new ArgumentNullException(nameof(options)) : Channel.CreateUnbounded<T>(options);

    /// <summary>Creates a prioritized channel using the supplied options.</summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="options">The prioritized channel configuration.</param>
    /// <returns>A configured prioritized channel instance.</returns>
    public static PrioritizedChannel<T> MakeChannel<T>(PrioritizedChannelOptions options) =>
        options is null ? throw new ArgumentNullException(nameof(options)) : new PrioritizedChannel<T>(options);

    /// <summary>
    /// Creates a prioritized channel using discrete configuration values.
    /// </summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="priorityLevels">The number of priority queues to maintain.</param>
    /// <param name="capacityPerLevel">The optional capacity to apply to each priority queue.</param>
    /// <param name="fullMode">The strategy used when the channel is full.</param>
    /// <param name="singleReader"><see langword="true"/> to optimize for a single reader; otherwise <see langword="false"/>.</param>
    /// <param name="singleWriter"><see langword="true"/> to optimize for a single writer; otherwise <see langword="false"/>.</param>
    /// <param name="defaultPriority">The optional default priority applied when enqueueing without an explicit value.</param>
    /// <returns>A configured prioritized channel instance.</returns>
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

    /// <summary>Represents a unit value used for Go-inspired helpers.</summary>
    public readonly record struct Unit
    {
        /// <summary>Gets a singleton instance representing the unit value.</summary>
        public static readonly Unit Value;
    }
}
