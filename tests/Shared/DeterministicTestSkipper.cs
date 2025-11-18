internal static class DeterministicTestSkipper
{
    private const string SkipEnvVar = "HUGO_SKIP_DETERMINISTIC_TESTS";

    public static bool ShouldSkip(out string? reason)
    {
        string? value = Environment.GetEnvironmentVariable(SkipEnvVar);
        if (!string.IsNullOrWhiteSpace(value) && IsTruthy(value))
        {
            reason = $"Skipping deterministic test because {SkipEnvVar}={value}.";
            return true;
        }

        reason = null;
        return false;
    }

    private static bool IsTruthy(string value)
    {
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
