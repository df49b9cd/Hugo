using System.Text;
using Hugo.Profiling;

namespace Hugo.Tests.Profiling;

public sealed class SpeedscopeAnalyzerTests
{
    [Fact]
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

        Assert.Equal(5d, report.TotalDurationMilliseconds, 3);
        Assert.Empty(report.Warnings);

        var profile = Assert.Single(report.Profiles);
        Assert.Equal("Main", profile.Name);
        Assert.Equal(5d, profile.DurationMilliseconds, 3);
        Assert.Equal("milliseconds", profile.Unit);
        Assert.Equal(4, profile.EventCount);

        var loop = Assert.Single(report.Frames, frame => frame.Name == "MainLoop");
        Assert.Equal(5d, loop.InclusiveMilliseconds, 3);
        Assert.Equal(3d, loop.SelfMilliseconds, 3);
        Assert.Equal(1, loop.CallCount);

        var worker = Assert.Single(report.Frames, frame => frame.Name == "Worker");
        Assert.Equal(2d, worker.InclusiveMilliseconds, 3);
        Assert.Equal(2d, worker.SelfMilliseconds, 3);
        Assert.Equal(1, worker.CallCount);
    }

    [Fact]
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

        Assert.NotEmpty(report.Warnings);
        Assert.Contains(report.Warnings, warning => warning.Contains("unclosed", StringComparison.OrdinalIgnoreCase));
    }
}
