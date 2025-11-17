using System.Text;
using Shouldly;

using Hugo.Profiling;

namespace Hugo.Tests.Profiling;

public sealed class SpeedscopeAnalyzerTests
{
    [Fact(Timeout = 15_000)]
    public void Analyze_ComputesFrameAndProfileSummaries()
    {
        var json = """
        {
          "shared": {
            "frames": [
              { "name": "MainLoop" },
              { "name": "Worker" }
            ]
          },
          "profiles": [
            {
              "type": "evented",
              "name": "Main",
              "unit": "milliseconds",
              "startValue": 0,
              "endValue": 5,
              "events": [
                { "type": "O", "frame": 0, "at": 0 },
                { "type": "O", "frame": 1, "at": 1 },
                { "type": "C", "frame": 1, "at": 3 },
                { "type": "C", "frame": 0, "at": 5 }
              ]
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var report = SpeedscopeAnalyzer.Analyze(stream);

        report.TotalDurationMilliseconds.ShouldBe(5d, 3);
        report.Warnings.ShouldBeEmpty();

        var profile = report.Profiles.ShouldHaveSingleItem();
        profile.Name.ShouldBe("Main");
        profile.DurationMilliseconds.ShouldBe(5d, 3);
        profile.Unit.ShouldBe("milliseconds");
        profile.EventCount.ShouldBe(4);

        var loop = report.Frames.ShouldHaveSingleItem(static frame => frame.Name == "MainLoop");
        loop.InclusiveMilliseconds.ShouldBe(5d, 3);
        loop.SelfMilliseconds.ShouldBe(3d, 3);
        loop.CallCount.ShouldBe(1);

        var worker = report.Frames.ShouldHaveSingleItem(static frame => frame.Name == "Worker");
        worker.InclusiveMilliseconds.ShouldBe(2d, 3);
        worker.SelfMilliseconds.ShouldBe(2d, 3);
        worker.CallCount.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public void Analyze_ReturnsWarningsForMismatchedEvents()
    {
        var json = """
        {
          "shared": {
            "frames": [
              { "name": "Aggregator" }
            ]
          },
          "profiles": [
            {
              "type": "evented",
              "name": "Broken",
              "unit": "milliseconds",
              "events": [
                { "type": "O", "frame": 0, "at": 0 }
              ]
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var report = SpeedscopeAnalyzer.Analyze(stream);

        report.Warnings.ShouldNotBeEmpty();
        static warning => warning.Contains("unclosed", StringComparison.OrdinalIgnoreCase).ShouldContain(report.Warnings);
    }
}
