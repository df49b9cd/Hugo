using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Hugo.Profiling;

public static class CounterAnalyzer
{
    public static CounterReport Analyze(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        using var stream = File.OpenRead(path);
        return Analyze(stream);
    }

    public static CounterReport Analyze(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        var builder = new CounterSeriesCollection();

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

        string? header = reader.ReadLine();
        if (header is null)
        {
            return new CounterReport(Array.Empty<CounterSummary>(), Array.Empty<string>(), null, null, 0, 0);
        }

        int lineNumber = 1;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            builder.IncrementTotalRows();

            if (!TrySplitColumns(line, out var parts))
            {
                builder.AddParseError($"Line {lineNumber}: expected 5 columns.");
                continue;
            }

            var timestampText = parts[0].Trim();
            if (!DateTime.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var timestamp))
            {
                builder.AddParseError($"Line {lineNumber}: unable to parse timestamp '{timestampText}'.");
                continue;
            }

            var provider = parts[1].Trim();
            var name = parts[2].Trim();
            var type = parts[3].Trim();
            var valueText = parts[4].Trim();

            if (!double.TryParse(valueText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
            {
                builder.AddParseError($"Line {lineNumber}: unable to parse value '{valueText}'.");
                continue;
            }

            builder.AddSample(timestamp, provider, name, type, value);
        }

        var report = builder.BuildReport();
        var findings = CounterHeuristics.Evaluate(report);
        return report with { Findings = findings };
    }

    private static bool TrySplitColumns(string line, out string[] parts)
    {
        parts = new string[5];
        int start = 0;
        int index = 0;

        for (int i = 0; i < line.Length && index < 4; i++)
        {
            if (line[i] == ',')
            {
                parts[index++] = line[start..i];
                start = i + 1;
            }
        }

        if (index < 4)
        {
            return false;
        }

        parts[4] = line[start..];
        return true;
    }

    private sealed class CounterSeriesCollection
    {
        private readonly Dictionary<string, CounterSeries> _series = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _errors = new();
        private DateTime? _firstTimestamp;
        private DateTime? _lastTimestamp;
        private int _totalRows;
        private int _parsedRows;

        public void IncrementTotalRows() => _totalRows++;

        public void AddParseError(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _errors.Add(message);
            }
        }

        public void AddSample(DateTime timestamp, string provider, string name, string type, double value)
        {
            var key = $"{provider}::{name}";
            if (!_series.TryGetValue(key, out var series))
            {
                series = new CounterSeries(provider, name, type);
                _series.Add(key, series);
            }

            series.Add(timestamp, value);
            _parsedRows++;

            if (!_firstTimestamp.HasValue || timestamp < _firstTimestamp)
            {
                _firstTimestamp = timestamp;
            }

            if (!_lastTimestamp.HasValue || timestamp > _lastTimestamp)
            {
                _lastTimestamp = timestamp;
            }
        }

        public CounterReport BuildReport()
        {
            var summaries = _series.Values
                .Select(series => series.ToSummary())
                .OrderBy(summary => summary.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CounterReport(
                summaries,
                _errors,
                _firstTimestamp,
                _lastTimestamp,
                _totalRows,
                _parsedRows);
        }
    }

    private sealed class CounterSeries
    {
        private readonly string _provider;
        private readonly string _name;
        private readonly string _type;
        private readonly List<double> _values = new();
        private readonly List<DateTime> _timestamps = new();
        private double _sum;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private double _latest;

        public CounterSeries(string provider, string name, string type)
        {
            _provider = provider;
            _name = name;
            _type = type;
        }

        public void Add(DateTime timestamp, double value)
        {
            _timestamps.Add(timestamp);
            _values.Add(value);
            _sum += value;

            if (value < _min)
            {
                _min = value;
            }

            if (value > _max)
            {
                _max = value;
            }

            _latest = value;
        }

        public CounterSummary ToSummary()
        {
            if (_values.Count == 0)
            {
                throw new InvalidOperationException("Cannot summarize an empty counter series.");
            }

            var sorted = _values.OrderBy(sample => sample).ToArray();

            double GetPercentile(double percentile)
            {
                if (sorted.Length == 1)
                {
                    return sorted[0];
                }

                double position = percentile * (sorted.Length - 1);
                int lowerIndex = (int)Math.Floor(position);
                int upperIndex = (int)Math.Ceiling(position);

                if (lowerIndex == upperIndex)
                {
                    return sorted[lowerIndex];
                }

                double weight = position - lowerIndex;
                return sorted[lowerIndex] + weight * (sorted[upperIndex] - sorted[lowerIndex]);
            }

            var firstTimestamp = _timestamps[0];
            var lastTimestamp = _timestamps[^1];

            return new CounterSummary(
                _provider,
                _name,
                _type,
                _values.Count,
                _min,
                _max,
                _sum / _values.Count,
                GetPercentile(0.95),
                GetPercentile(0.99),
                _latest,
                _sum,
                firstTimestamp,
                lastTimestamp);
        }
    }
}
