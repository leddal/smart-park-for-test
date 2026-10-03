namespace SmartPark.Benchmarks;

public sealed class BenchmarkRunResult
{
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; set; }
    public required int Seed { get; init; }
    public required string SourceHash { get; init; }
    public required BenchmarkEnvironment Environment { get; init; }
    public required Dictionary<string, string> DependencyVersions { get; init; }
    public required Dictionary<string, object?> InputParameters { get; init; }
    public List<BenchmarkScenarioResult> Scenarios { get; } = [];
}

public sealed class BenchmarkEnvironment
{
    public required string OsDescription { get; init; }
    public required string FrameworkDescription { get; init; }
    public required string ProcessArchitecture { get; init; }
    public required int LogicalProcessors { get; init; }
    public required long AvailableMemoryBytes { get; init; }
    public required string PostgreSqlVersion { get; init; }
    public required string RedisVersion { get; init; }
    public required string ContainerMemoryLimit { get; init; }
}

public sealed class BenchmarkScenarioResult
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Dictionary<string, object?> Parameters { get; init; }
    public required Dictionary<string, object?> Verification { get; init; }
    public required Dictionary<string, double?> Metrics { get; init; }
    public required List<BenchmarkSample> Samples { get; init; }
    public required List<string> Artifacts { get; init; }
    public required List<string> Notes { get; init; }
}

public sealed record BenchmarkSample(
    string Variant,
    string Phase,
    int Sequence,
    double ElapsedMilliseconds,
    int MaterializedRows,
    int? PayloadUtf8Bytes = null,
    long? DbCommandCount = null,
    string? CacheOutcome = null);

public static class BenchmarkMetrics
{
    public static double? Reduction(double baseline, double optimized) => baseline == 0 ? null : (baseline - optimized) / baseline * 100d;
    public static double? Factor(double baseline, double optimized) => optimized == 0 ? null : baseline / optimized;

    public static double Median(IEnumerable<double> samples)
    {
        var ordered = samples.Order().ToArray();
        if (ordered.Length == 0) throw new InvalidOperationException("Cannot calculate a median from no samples.");
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2d;
    }

    public static double Percentile95(IEnumerable<double> samples)
    {
        var ordered = samples.Order().ToArray();
        if (ordered.Length == 0) throw new InvalidOperationException("Cannot calculate P95 from no samples.");
        var index = (int)Math.Ceiling(0.95d * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    public static double Spread(IEnumerable<double> samples)
    {
        var ordered = samples.Order().ToArray();
        if (ordered.Length == 0) throw new InvalidOperationException("Cannot calculate spread from no samples.");
        return ordered[^1] - ordered[0];
    }
}
