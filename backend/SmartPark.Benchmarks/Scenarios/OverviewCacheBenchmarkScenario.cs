using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SmartPark.Api.Features.Overview;

namespace SmartPark.Benchmarks.Scenarios;

public sealed class OverviewCacheBenchmarkScenario(BenchmarkServiceHost host, DbCommandCounter commandCounter, BenchmarkOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BenchmarkScenarioResult> RunAsync(CancellationToken ct)
    {
        var direct = await FetchDirectAsync(ct);
        if (direct.SimulationEnabled)
            throw new InvalidOperationException("Overview cache benchmark requires simulation to be stopped so the input remains frozen.");

        await InvalidateCacheAsync(ct);
        var cold = await MeasureCachedAsync("redisCache", "cold", 0, ct);
        EnsureRedis(cold.CacheStatus);
        EnsureEquivalent(direct, cold.Snapshot);

        var snapshotFile = Path.Combine("cache-normalized-snapshot.json");
        await File.WriteAllTextAsync(Path.Combine(options.RunDirectory, snapshotFile), Normalize(cold.Snapshot).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

        var samples = new List<BenchmarkSample> { cold.Sample };
        for (var iteration = 0; iteration < BenchmarkOptions.WarmupIterations; iteration++)
        {
            var database = await MeasureDirectAsync("databaseBypass", "warmup", iteration, ct);
            var cached = await MeasureCachedAsync("redisCache", "warmup", iteration, ct);
            EnsureRedis(cached.CacheStatus);
            EnsureEquivalent(database.Snapshot, cached.Snapshot);
            samples.Add(database.Sample);
            samples.Add(cached.Sample);
        }
        for (var iteration = 0; iteration < BenchmarkOptions.SampleIterations; iteration++)
        {
            var database = await MeasureDirectAsync("databaseBypass", "measured", iteration, ct);
            var cached = await MeasureCachedAsync("redisCache", "measured", iteration, ct);
            EnsureRedis(cached.CacheStatus);
            EnsureEquivalent(database.Snapshot, cached.Snapshot);
            samples.Add(database.Sample);
            samples.Add(cached.Sample);
        }

        var baseline = samples.Where(x => x.Phase == "measured" && x.Variant == "databaseBypass").ToList();
        var cachedSamples = samples.Where(x => x.Phase == "measured" && x.Variant == "redisCache").ToList();
        var baselineMedian = BenchmarkMetrics.Median(baseline.Select(x => x.ElapsedMilliseconds));
        var cachedMedian = BenchmarkMetrics.Median(cachedSamples.Select(x => x.ElapsedMilliseconds));
        var baselineP95 = BenchmarkMetrics.Percentile95(baseline.Select(x => x.ElapsedMilliseconds));
        var cachedP95 = BenchmarkMetrics.Percentile95(cachedSamples.Select(x => x.ElapsedMilliseconds));
        var cacheHits = cachedSamples.Count(x => x.CacheOutcome == "inferredHitFromEfDbCommandCounter");
        var cacheMisses = cachedSamples.Count(x => x.CacheOutcome == "inferredMissFromEfDbCommandCounter");

        return new BenchmarkScenarioResult
        {
            Name = "overview_redis_cache",
            Description = "Compares the actual OverviewQueryService database path with the actual OverviewCacheService Redis path on a frozen overview response. The API cache uses its production 10-second TTL.",
            Parameters = new Dictionary<string, object?>
            {
                ["warmupPairs"] = BenchmarkOptions.WarmupIterations,
                ["measuredInterleavedPairs"] = BenchmarkOptions.SampleIterations,
                ["coldSamples"] = 1,
                ["cacheTtlSeconds"] = 10,
                ["baselineService"] = "OverviewQueryService.GetAsync(CancellationToken)",
                ["optimizedService"] = "OverviewCacheService.GetAsync(CancellationToken)",
                ["cacheHitMissMethod"] = "inferred from EF DbCommandInterceptor activity because OverviewCacheService exposes no hit/miss counters"
            },
            Verification = new Dictionary<string, object?>
            {
                ["simulationStopped"] = true,
                ["normalizedSnapshotEquality"] = true,
                ["ignoredRuntimeMetadata"] = new[] { "generatedAt", "cacheStatus" },
                ["coldDbCommandCount"] = cold.Sample.DbCommandCount,
                ["measuredCacheInferredHits"] = cacheHits,
                ["measuredCacheInferredMisses"] = cacheMisses,
                ["averageBaselineDbCommandCount"] = baseline.Average(x => x.DbCommandCount ?? 0),
                ["averageCachedDbCommandCount"] = cachedSamples.Average(x => x.DbCommandCount ?? 0)
            },
            Metrics = new Dictionary<string, double?>
            {
                ["coldMilliseconds"] = cold.Sample.ElapsedMilliseconds,
                ["baselineMedianMilliseconds"] = baselineMedian,
                ["cachedMedianMilliseconds"] = cachedMedian,
                ["baselineP95Milliseconds"] = baselineP95,
                ["cachedP95Milliseconds"] = cachedP95,
                ["baselineSpreadMilliseconds"] = BenchmarkMetrics.Spread(baseline.Select(x => x.ElapsedMilliseconds)),
                ["cachedSpreadMilliseconds"] = BenchmarkMetrics.Spread(cachedSamples.Select(x => x.ElapsedMilliseconds)),
                ["medianReductionPercent"] = BenchmarkMetrics.Reduction(baselineMedian, cachedMedian),
                ["medianFactor"] = BenchmarkMetrics.Factor(baselineMedian, cachedMedian),
                ["measuredDbCommandReductionPercent"] = BenchmarkMetrics.Reduction(baseline.Sum(x => x.DbCommandCount ?? 0), cachedSamples.Sum(x => x.DbCommandCount ?? 0))
            },
            Samples = samples,
            Artifacts = [snapshotFile],
            Notes = ["The cache service has no hit/miss statistic. A cache call with zero EF database commands is labeled as an inferred hit; a call that executes EF commands is labeled as an inferred miss.", "Cached output is verified against the direct service response after removing only generatedAt and cacheStatus runtime metadata."]
        };
    }

    private async Task<OverviewSnapshot> FetchDirectAsync(CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<OverviewQueryService>();
        return await service.GetAsync(ct);
    }

    private async Task<OverviewMeasurement> MeasureDirectAsync(string variant, string phase, int sequence, CancellationToken ct)
    {
        commandCounter.Reset();
        var stopwatch = Stopwatch.StartNew();
        var snapshot = await FetchDirectAsync(ct);
        stopwatch.Stop();
        var dbCommandCount = commandCounter.Value;
        return new OverviewMeasurement(snapshot, "Database", dbCommandCount, new BenchmarkSample(variant, phase, sequence, stopwatch.Elapsed.TotalMilliseconds, 1, null, dbCommandCount));
    }

    private async Task<OverviewMeasurement> MeasureCachedAsync(string variant, string phase, int sequence, CancellationToken ct)
    {
        commandCounter.Reset();
        var stopwatch = Stopwatch.StartNew();
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<OverviewCacheService>();
        var snapshot = await service.GetAsync(ct);
        stopwatch.Stop();
        var dbCommandCount = commandCounter.Value;
        var outcome = dbCommandCount == 0 ? "inferredHitFromEfDbCommandCounter" : "inferredMissFromEfDbCommandCounter";
        return new OverviewMeasurement(snapshot, service.Status, dbCommandCount, new BenchmarkSample(variant, phase, sequence, stopwatch.Elapsed.TotalMilliseconds, 1, null, dbCommandCount, outcome));
    }

    private async Task InvalidateCacheAsync(CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<OverviewCacheService>();
        await service.InvalidateAsync(ct);
    }

    private static void EnsureRedis(string status)
    {
        if (!string.Equals(status, "Redis", StringComparison.Ordinal))
            throw new InvalidOperationException($"Overview cache benchmark requires the actual Redis cache, but OverviewCacheService reported '{status}'.");
    }

    private static void EnsureEquivalent(OverviewSnapshot direct, OverviewSnapshot cached)
    {
        if (!JsonNode.DeepEquals(Normalize(direct), Normalize(cached)))
            throw new InvalidOperationException("Overview cache benchmark inconsistency: direct and cached snapshots differ after removing only runtime cacheStatus/generatedAt metadata.");
    }

    private static JsonNode Normalize(OverviewSnapshot snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, JsonOptions) ?? throw new InvalidOperationException("Could not serialize overview snapshot for equality verification.");
        RemoveRuntimeMetadata(node);
        return node;
    }

    private static void RemoveRuntimeMetadata(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("generatedAt");
            obj.Remove("cacheStatus");
            foreach (var child in obj.ToList()) if (child.Value is not null) RemoveRuntimeMetadata(child.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array) if (child is not null) RemoveRuntimeMetadata(child);
        }
    }

    private sealed record OverviewMeasurement(OverviewSnapshot Snapshot, string CacheStatus, long DbCommandCount, BenchmarkSample Sample);
}
