using Microsoft.Testing.Platform.Builder;

namespace Xunit.Runner.InProc.SystemConsole.TestingPlatform;

/// <summary>
/// Temporary shim that forwards Microsoft Testing Platform entry points to xUnit's implementation until the SystemConsole runner adds one.
/// </summary>
internal static class TestPlatformTestFramework
{
    public static Task<int> RunAsync(string[] args, Action<ITestApplicationBuilder, string[]> configureBuilder)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configureBuilder);
        return global::Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, configureBuilder);
    }
}
