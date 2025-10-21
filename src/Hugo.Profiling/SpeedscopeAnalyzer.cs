using System.Text.Json;

namespace Hugo.Profiling;

public sealed record SpeedscopeFrameSummary(string Name, double InclusiveMilliseconds, double SelfMilliseconds, int CallCount);

public sealed record SpeedscopeProfileSummary(string Name, double DurationMilliseconds, string Unit, int EventCount);

public sealed record SpeedscopeReport(
    IReadOnlyList<SpeedscopeFrameSummary> Frames,
    IReadOnlyList<SpeedscopeProfileSummary> Profiles,
    IReadOnlyList<string> Warnings,
    double TotalDurationMilliseconds);

public static class SpeedscopeAnalyzer
{
    public static SpeedscopeReport Analyze(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        using var stream = File.OpenRead(path);
        return Analyze(stream);
    }

    public static SpeedscopeReport Analyze(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        if (!root.TryGetProperty("shared", out var shared) || !shared.TryGetProperty("frames", out var framesElement))
        {
            throw new InvalidDataException("Speedscope JSON does not contain shared frames.");
        }

        var frameNames = new List<string>();
        foreach (var frame in framesElement.EnumerateArray())
        {
            frameNames.Add(frame.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? $"Frame {frameNames.Count}"
                : $"Frame {frameNames.Count}");
        }

        var frameAggregates = new Dictionary<int, FrameAggregate>(capacity: frameNames.Count);
        var warnings = new List<string>();
        var profileSummaries = new List<SpeedscopeProfileSummary>();
        double totalDurationMilliseconds = 0;

        if (!root.TryGetProperty("profiles", out var profilesElement))
        {
            return new SpeedscopeReport(Array.Empty<SpeedscopeFrameSummary>(), Array.Empty<SpeedscopeProfileSummary>(), Array.Empty<string>(), 0);
        }

    foreach (var profile in profilesElement.EnumerateArray())
        {
            var type = profile.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (!string.Equals(type, "evented", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var unit = profile.TryGetProperty("unit", out var unitElement) ? unitElement.GetString() ?? "milliseconds" : "milliseconds";
            var scale = UnitToMilliseconds(unit);

            double durationMilliseconds = 0;
            if (profile.TryGetProperty("startValue", out var startValueElement) && profile.TryGetProperty("endValue", out var endValueElement))
            {
                durationMilliseconds = Math.Max(0, (endValueElement.GetDouble() - startValueElement.GetDouble()) * scale);
            }

            string profileName = profile.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "Unnamed profile" : "Unnamed profile";

            if (!profile.TryGetProperty("events", out var eventsElement))
            {
                warnings.Add($"Profile '{profileName}' does not include events.");
                continue;
            }

            var stack = new List<StackEntry>();
            int openEvents = 0;
            int closeEvents = 0;
            bool hasEvent = false;
            double firstTimestamp = 0;
            double lastTimestamp = 0;
            int eventCount = 0;

            foreach (var evt in eventsElement.EnumerateArray())
            {
                eventCount++;
                if (!evt.TryGetProperty("type", out var eventTypeElement))
                {
                    continue;
                }

                var eventType = eventTypeElement.GetString();
                if (!evt.TryGetProperty("at", out var atElement))
                {
                    continue;
                }

                double timestamp = atElement.GetDouble() * scale;
                if (!hasEvent)
                {
                    firstTimestamp = timestamp;
                    hasEvent = true;
                }

                lastTimestamp = timestamp;

                switch (eventType)
                {
                    case "O":
                        if (!evt.TryGetProperty("frame", out var openFrameElement))
                        {
                            warnings.Add($"Profile '{profileName}' contains an open event without a frame index.");
                            continue;
                        }

                        openEvents++;
                        stack.Add(new StackEntry(openFrameElement.GetInt32(), timestamp));
                        break;

                    case "C":
                        if (!evt.TryGetProperty("frame", out var closeFrameElement))
                        {
                            warnings.Add($"Profile '{profileName}' contains a close event without a frame index.");
                            continue;
                        }

                        closeEvents++;
                        if (stack.Count == 0)
                        {
                            warnings.Add($"Profile '{profileName}' closed frame {closeFrameElement.GetInt32()} without a matching open event.");
                            continue;
                        }

                        var entryIndex = stack.Count - 1;
                        var entry = stack[entryIndex];
                        if (entry.FrameIndex != closeFrameElement.GetInt32())
                        {
                            warnings.Add($"Profile '{profileName}' encountered mismatched close event for frame {closeFrameElement.GetInt32()} (expected {entry.FrameIndex}).");
                            stack.RemoveAt(entryIndex);
                            continue;
                        }

                        stack.RemoveAt(entryIndex);
                        var duration = Math.Max(0, timestamp - entry.StartTimestamp);
                        var selfDuration = Math.Max(0, duration - entry.ChildDuration);

                        var aggregate = GetAggregate(frameAggregates, closeFrameElement.GetInt32());
                        aggregate.InclusiveMilliseconds += duration;
                        aggregate.SelfMilliseconds += selfDuration;
                        aggregate.CallCount++;

                        if (stack.Count > 0)
                        {
                            var parentIndex = stack.Count - 1;
                            var parent = stack[parentIndex];
                            parent.ChildDuration += duration;
                            stack[parentIndex] = parent;
                        }

                        break;
                }
            }

            if (stack.Count > 0)
            {
                warnings.Add($"Profile '{profileName}' ended with {stack.Count} unclosed frames.");
            }

            if (durationMilliseconds <= 0 && hasEvent)
            {
                durationMilliseconds = Math.Max(0, lastTimestamp - firstTimestamp);
            }

            totalDurationMilliseconds += durationMilliseconds;
            profileSummaries.Add(new SpeedscopeProfileSummary(profileName, durationMilliseconds, unit, eventCount));

            if (openEvents != closeEvents)
            {
                warnings.Add($"Profile '{profileName}' has {openEvents} open events and {closeEvents} close events.");
            }
        }

        var summaries = frameAggregates.Select(pair =>
            {
                var name = pair.Key >= 0 && pair.Key < frameNames.Count ? frameNames[pair.Key] : $"Frame {pair.Key}";
                return new SpeedscopeFrameSummary(name, pair.Value.InclusiveMilliseconds, pair.Value.SelfMilliseconds, pair.Value.CallCount);
            })
            .OrderByDescending(summary => summary.InclusiveMilliseconds)
            .ThenBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SpeedscopeReport(summaries, profileSummaries, warnings, totalDurationMilliseconds);
    }

    private static FrameAggregate GetAggregate(IDictionary<int, FrameAggregate> aggregates, int frameIndex)
    {
        if (!aggregates.TryGetValue(frameIndex, out var aggregate))
        {
            aggregate = new FrameAggregate();
            aggregates[frameIndex] = aggregate;
        }

        return aggregate;
    }

    private static double UnitToMilliseconds(string unit)
    {
        return unit switch
        {
            "seconds" => 1_000d,
            "microseconds" => 0.001d,
            "nanoseconds" => 0.000001d,
            "milliseconds" => 1d,
            _ => 1d
        };
    }

    private struct StackEntry(int frameIndex, double startTimestamp)
    {
        public int FrameIndex { get; } = frameIndex;
        public double StartTimestamp { get; } = startTimestamp;
        public double ChildDuration { get; set; } = 0;
    }

    private sealed class FrameAggregate
    {
        public double InclusiveMilliseconds;
        public double SelfMilliseconds;
        public int CallCount;
    }
}
