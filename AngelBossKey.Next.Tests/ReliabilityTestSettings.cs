using System.Globalization;
using System.Text.Json;

namespace AngelBossKey.Next.Tests;

internal static class ReliabilityTestSettings
{
    private static readonly JsonSerializerOptions TraceJsonOptions = new() { WriteIndented = true };

    internal static int SeedCount => ReadBoundedInt("ANGEL_RELIABILITY_SEEDS", 12, 1, 4096);
    internal static int StepCount => ReadBoundedInt("ANGEL_RELIABILITY_STEPS", 80, 1, 100_000);
    internal static int BaseSeed => ReadInt("ANGEL_RELIABILITY_BASE_SEED", 4_301_281);
    internal static int WindowVisibilityCycles =>
        ReadBoundedInt("ANGEL_WINDOW_VISIBILITY_CYCLES", 100, 1, 10_000);

    internal static void WriteFailureTrace(
        int seed,
        int configuredSteps,
        IReadOnlyCollection<string> operations,
        Exception exception)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("ANGEL_RELIABILITY_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        try
        {
            var fullDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullDirectory);
            var path = Path.Combine(fullDirectory, $"audio-model-{unchecked((uint)seed):X8}.json");
            var document = new
            {
                Seed = seed,
                ConfiguredSteps = configuredSteps,
                Exception = exception.GetType().FullName,
                exception.Message,
                Operations = operations
            };
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(document, TraceJsonOptions));
        }
        catch (Exception traceException) when (
            traceException is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Failure reporting must not replace the original reliability failure.
        }
    }

    private static int ReadBoundedInt(string name, int defaultValue, int minimum, int maximum)
    {
        var value = ReadInt(name, defaultValue);
        return value >= minimum && value <= maximum
            ? value
            : throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
    }

    private static int ReadInt(string name, int defaultValue)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{name} must be a 32-bit integer.");
    }
}
