using Xunit;

namespace Hugo.Tests;

internal static class TestCollections
{
    internal const string DiagnosticsIsolation = "DiagnosticsIsolation";
}

[CollectionDefinition(TestCollections.DiagnosticsIsolation, DisableParallelization = true)]
public sealed class DiagnosticsIsolationCollection
{
}
