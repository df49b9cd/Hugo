using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hugo.Profiling;

namespace Hugo.ProfilingAnalyzer;

internal static class Program
{
    private static readonly string[] SupportedSortColumns = ["max", "mean", "p95", "sum"];

    internal static async Task<int> Main(string[] args)
    {
        var command = BuildRootCommand();
        return await command.Parse(args).InvokeAsync();
    }

    private static RootCommand BuildRootCommand()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Path to a profiling baseline folder or counters CSV file."
        };

        var topOption = new Option<int>("--top")
        {
            Description = "Number of counters to display in the summary table.",
            DefaultValueFactory = _ => 12
        };

        var includeSystemOption = new Option<bool>("--include-system")
        {
            Description = "Include System.Runtime counters in the summary table."
        };

        var sortOption = new Option<string>("--sort")
        {
            Description = "Sort counters by 'max', 'mean', 'p95', or 'sum'.",
            DefaultValueFactory = _ => "max"
        };

        var providerOption = new Option<string?>("--provider")
        {
            Description = "Filter counters by provider substring (case-insensitive)."
        };

        var counterOption = new Option<string?>("--counter")
        {
            Description = "Filter counters by counter name substring (case-insensitive)."
        };

        var findingsOnlyOption = new Option<bool>("--findings-only")
        {
            Description = "Only print heuristic findings (skip the counter table)."
        };

        var root = new RootCommand("Analyze Hugo profiling baselines")
        {
            pathArgument,
            topOption,
            includeSystemOption,
            sortOption,
            providerOption,
            counterOption,
            findingsOnlyOption
        };

        root.SetAction((ParseResult parseResult, CancellationToken _) =>
        {
            try
            {
                return Task.FromResult(Run(parseResult, pathArgument, topOption, includeSystemOption, sortOption, providerOption, counterOption, findingsOnlyOption));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return Task.FromResult(1);
            }
        });

        return root;
    }

    private static int Run(
        ParseResult parseResult,
        Argument<string> pathArgument,
        Option<int> topOption,
        Option<bool> includeSystemOption,
        Option<string> sortOption,
        Option<string?> providerOption,
        Option<string?> counterOption,
        Option<bool> findingsOnlyOption)
    {
        var path = parseResult.GetValue(pathArgument);
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("error: path argument is required.");
            return 1;
        }

        var top = Math.Max(1, parseResult.GetValue(topOption));
        var includeSystem = parseResult.GetValue(includeSystemOption);
    var sortColumn = parseResult.GetValue(sortOption) ?? "max";
        var providerFilter = parseResult.GetValue(providerOption);
        var counterFilter = parseResult.GetValue(counterOption);
        var findingsOnly = parseResult.GetValue(findingsOnlyOption);

        if (!SupportedSortColumns.Contains(sortColumn, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"error: unsupported --sort value '{sortColumn}'. Supported values: {string.Join(", ", SupportedSortColumns)}.");
            return 1;
        }

        var countersPath = ResolveCountersPath(path);
        var report = CounterAnalyzer.Analyze(countersPath);

        PrintSummary(countersPath, report);
        PrintFindings(report);

        if (!findingsOnly)
        {
            PrintCounterTable(report, top, includeSystem, providerFilter, counterFilter, sortColumn);
        }

        PrintParseDiagnostics(report);
        return 0;
    }

    private static void PrintSummary(string countersPath, CounterReport report)
    {
        var durationText = report.Duration > TimeSpan.Zero
            ? $"{report.Duration.TotalSeconds:0.##}s"
            : "n/a";
        Console.WriteLine($"Source: {countersPath}");
        Console.WriteLine($"Duration: {durationText}; Counters: {report.Counters.Count}; Rows parsed: {report.ParsedRows}/{report.TotalRows}");
    }

    private static void PrintFindings(CounterReport report)
    {
        Console.WriteLine();
        Console.WriteLine("Findings:");

        if (report.Findings.Count == 0)
        {
            Console.WriteLine("  None detected. Baseline looks healthy.");
            return;
        }

        foreach (var finding in report.Findings)
        {
            var label = finding.Severity switch
            {
                AnalyzerSeverity.Critical => "[CRITICAL]",
                AnalyzerSeverity.Warning => "[WARNING]",
                _ => "[INFO]"
            };

            Console.WriteLine($"  {label} {finding.Counter}: {finding.Message}");
        }
    }

    private static void PrintCounterTable(CounterReport report, int top, bool includeSystem, string? providerFilter, string? counterFilter, string sortColumn)
    {
        var filtered = report.Counters.AsEnumerable();

        if (!includeSystem)
        {
            filtered = filtered.Where(series => !string.Equals(series.Provider, "System.Runtime", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(providerFilter))
        {
            filtered = filtered.Where(series => series.Provider.Contains(providerFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(counterFilter))
        {
            filtered = filtered.Where(series => series.Name.Contains(counterFilter, StringComparison.OrdinalIgnoreCase));
        }

        filtered = sortColumn.ToLowerInvariant() switch
        {
            "mean" => filtered.OrderByDescending(series => series.Mean),
            "p95" => filtered.OrderByDescending(series => series.P95),
            "sum" => filtered.OrderByDescending(series => series.Sum),
            _ => filtered.OrderByDescending(series => series.Max)
        };

        var counters = filtered.Take(top).ToList();

        Console.WriteLine();
        Console.WriteLine("Counters:");

        if (counters.Count == 0)
        {
            if (!includeSystem && report.Counters.Any(series => string.Equals(series.Provider, "System.Runtime", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("  No counters matched the provided filters. Re-run with --include-system to inspect runtime counters.");
            }
            else
            {
                Console.WriteLine("  No counters matched the provided filters.");
            }
            return;
        }

        int providerWidth = Math.Min(24, Math.Max("Provider".Length, counters.Select(series => series.Provider.Length).DefaultIfEmpty(0).Max()));
        int nameWidth = Math.Min(60, Math.Max("Counter".Length, counters.Select(series => series.Name.Length).DefaultIfEmpty(0).Max()));

        Console.WriteLine(
            $"  {PadRight("Provider", providerWidth)} {PadRight("Counter", nameWidth)} {PadRight("Type", 8)} {PadLeft("Samples", 8)} {PadLeft("Min", 12)} {PadLeft("Mean", 12)} {PadLeft("P95", 12)} {PadLeft("Max", 12)} {PadLeft("Latest", 12)} {PadLeft("Sum", 12)}");

        foreach (var series in counters)
        {
            Console.WriteLine(
                $"  {PadRight(series.Provider, providerWidth)} {PadRight(series.Name, nameWidth)} {PadRight(series.Type, 8)} {PadLeft(series.Samples.ToString(CultureInfo.InvariantCulture), 8)} {PadLeft(FormatNumber(series.Min), 12)} {PadLeft(FormatNumber(series.Mean), 12)} {PadLeft(FormatNumber(series.P95), 12)} {PadLeft(FormatNumber(series.Max), 12)} {PadLeft(FormatNumber(series.Latest), 12)} {PadLeft(FormatNumber(series.Sum), 12)}");
        }
    }

    private static void PrintParseDiagnostics(CounterReport report)
    {
        if (report.ParseErrors.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Parse diagnostics:");

        foreach (var error in report.ParseErrors.Take(10))
        {
            Console.WriteLine($"  - {error}");
        }

        if (report.ParseErrors.Count > 10)
        {
            Console.WriteLine($"  ... {report.ParseErrors.Count - 10} additional errors");
        }
    }

    private static string ResolveCountersPath(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (Directory.Exists(path))
        {
            var explicitCandidate = Path.Combine(path, "counters.csv");
            if (File.Exists(explicitCandidate))
            {
                return explicitCandidate;
            }

            var firstCsv = Directory.EnumerateFiles(path, "*.csv", SearchOption.TopDirectoryOnly)
                .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (firstCsv != null)
            {
                return firstCsv;
            }
        }

        throw new FileNotFoundException($"No counters CSV found at '{path}'.");
    }

    private static string PadRight(string value, int width)
    {
        if (value.Length > width)
        {
            return value[..Math.Max(1, width - 1)] + "…";
        }

        return value.PadRight(width);
    }

    private static string PadLeft(string value, int width)
    {
        if (value.Length > width)
        {
            return value[^Math.Max(1, width - 1)..] + "…";
        }

        return value.PadLeft(width);
    }

    private static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "-";
        }

        var abs = Math.Abs(value);

        if (abs == 0)
        {
            return "0";
        }

        if (abs < 0.0001)
        {
            return value.ToString("0.###E0", CultureInfo.InvariantCulture);
        }

        if (abs < 1)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        if (abs < 1_000)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        if (abs < 1_000_000)
        {
            return (value / 1_000).ToString("0.##K", CultureInfo.InvariantCulture);
        }

        if (abs < 1_000_000_000)
        {
            return (value / 1_000_000).ToString("0.##M", CultureInfo.InvariantCulture);
        }

        return (value / 1_000_000_000).ToString("0.##B", CultureInfo.InvariantCulture);
    }
}
