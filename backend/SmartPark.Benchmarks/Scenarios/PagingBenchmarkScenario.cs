using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartPark.Api.Common;
using SmartPark.Api.Data;

namespace SmartPark.Benchmarks.Scenarios;

public sealed class PagingBenchmarkScenario(BenchmarkServiceHost host, DbCommandCounter commandCounter, BenchmarkOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string BenchmarkCategory => $"BenchmarkPaging-{options.RunId}";

    public async Task<BenchmarkScenarioResult> RunAsync(CancellationToken ct)
    {
        await SeedAssetsAsync(ct);

        var full = await FetchAllThenTakeAsync(ct);
        var page = await FetchServerPageAsync(ct);
        var firstTwentyMatch = JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(full.FirstTwenty, JsonOptions),
            JsonSerializer.SerializeToNode(page.FirstTwenty, JsonOptions));
        if (!firstTwentyMatch)
            throw new InvalidOperationException("Paging benchmark inconsistency: the first 20 assets differ between full fetch and server paging.");
        if (full.MaterializedRows != BenchmarkOptions.PagingAssetCount || page.MaterializedRows != 20)
            throw new InvalidOperationException($"Paging benchmark inconsistency: expected {BenchmarkOptions.PagingAssetCount}/20 materialized rows but observed {full.MaterializedRows}/{page.MaterializedRows}.");

        var samples = new List<BenchmarkSample>();
        for (var iteration = 0; iteration < BenchmarkOptions.WarmupIterations; iteration++)
        {
            samples.Add(await MeasureAsync("allFetchThenTake20", "warmup", iteration, FetchAllThenTakeAsync, ct));
            samples.Add(await MeasureAsync("serverPage20", "warmup", iteration, FetchServerPageAsync, ct));
        }
        for (var iteration = 0; iteration < BenchmarkOptions.SampleIterations; iteration++)
        {
            samples.Add(await MeasureAsync("allFetchThenTake20", "measured", iteration, FetchAllThenTakeAsync, ct));
            samples.Add(await MeasureAsync("serverPage20", "measured", iteration, FetchServerPageAsync, ct));
        }

        var baseline = samples.Where(x => x.Phase == "measured" && x.Variant == "allFetchThenTake20").ToList();
        var optimized = samples.Where(x => x.Phase == "measured" && x.Variant == "serverPage20").ToList();
        var baselineMedian = BenchmarkMetrics.Median(baseline.Select(x => x.ElapsedMilliseconds));
        var optimizedMedian = BenchmarkMetrics.Median(optimized.Select(x => x.ElapsedMilliseconds));
        var baselineP95 = BenchmarkMetrics.Percentile95(baseline.Select(x => x.ElapsedMilliseconds));
        var optimizedP95 = BenchmarkMetrics.Percentile95(optimized.Select(x => x.ElapsedMilliseconds));
        var baselinePayload = full.PayloadUtf8Bytes;
        var optimizedPayload = page.PayloadUtf8Bytes;

        return new BenchmarkScenarioResult
        {
            Name = "paging",
            Description = "Loads all 10,000 benchmark assets and takes 20 in memory versus the production paging pattern (count plus ordered server-side Take(20)) with the same asset projection.",
            Parameters = new Dictionary<string, object?>
            {
                ["assetCount"] = BenchmarkOptions.PagingAssetCount,
                ["benchmarkCategory"] = BenchmarkCategory,
                ["pageSize"] = 20,
                ["warmupIterationsPerVariant"] = BenchmarkOptions.WarmupIterations,
                ["measuredIterationsPerVariant"] = BenchmarkOptions.SampleIterations,
                ["ordering"] = "Asset.Code ascending",
                ["projection"] = "asset list row projection matching AssetsController.List"
            },
            Verification = new Dictionary<string, object?>
            {
                ["firstTwentyExactEquality"] = true,
                ["baselineMaterializedRows"] = full.MaterializedRows,
                ["optimizedMaterializedRows"] = page.MaterializedRows,
                ["optimizedTotalCount"] = page.TotalCount,
                ["baselinePayloadUtf8Bytes"] = baselinePayload,
                ["optimizedPayloadUtf8Bytes"] = optimizedPayload
            },
            Metrics = new Dictionary<string, double?>
            {
                ["baselineMedianMilliseconds"] = baselineMedian,
                ["optimizedMedianMilliseconds"] = optimizedMedian,
                ["baselineP95Milliseconds"] = baselineP95,
                ["optimizedP95Milliseconds"] = optimizedP95,
                ["baselineSpreadMilliseconds"] = BenchmarkMetrics.Spread(baseline.Select(x => x.ElapsedMilliseconds)),
                ["optimizedSpreadMilliseconds"] = BenchmarkMetrics.Spread(optimized.Select(x => x.ElapsedMilliseconds)),
                ["medianReductionPercent"] = BenchmarkMetrics.Reduction(baselineMedian, optimizedMedian),
                ["medianFactor"] = BenchmarkMetrics.Factor(baselineMedian, optimizedMedian),
                ["payloadReductionPercent"] = BenchmarkMetrics.Reduction(baselinePayload, optimizedPayload),
                ["payloadFactor"] = BenchmarkMetrics.Factor(baselinePayload, optimizedPayload)
            },
            Samples = samples,
            Artifacts = [],
            Notes = ["Payload measurements are actual UTF-8 JSON bytes. This contrast measures database materialization and first-screen payload only; it does not make a frame-rate claim."]
        };
    }

    private async Task SeedAssetsAsync(CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>();
        var existing = await db.Assets.CountAsync(x => x.Category == BenchmarkCategory, ct);
        if (existing != 0)
            throw new InvalidOperationException($"Paging benchmark requires no existing '{BenchmarkCategory}' assets in this isolated database, but found {existing}.");

        for (var start = 0; start < BenchmarkOptions.PagingAssetCount; start += 500)
        {
            var batch = Enumerable.Range(start, Math.Min(500, BenchmarkOptions.PagingAssetCount - start))
                .Select(index => new Asset
                {
                    Code = $"BENCH-PAGE-{options.RunId}-{index:D5}",
                    Name = $"Benchmark paging asset {index:D5}",
                    Category = BenchmarkCategory,
                    Status = "Active",
                    PublicCode = $"BENCH-PUBLIC-{options.RunId}-{index:D5}",
                    PublicDescription = "Synthetic benchmark asset"
                });
            db.Assets.AddRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    private async Task<MeasuredPayload> FetchAllThenTakeAsync(CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>();
        var rows = await AssetRows(db).ToListAsync(ct);
        var firstTwenty = rows.Take(20).ToList();
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(rows, JsonOptions));
        return new MeasuredPayload(rows.Count, rows.Count, firstTwenty, bytes);
    }

    private async Task<MeasuredPayload> FetchServerPageAsync(CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>();
        var page = await AssetRows(db).ToPageAsync(1, 20, ct);
        var firstTwenty = page.Items.ToList();
        var response = new PageResult<AssetRow>(page.Items, page.Total, page.Page, page.PageSize);
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(response, JsonOptions));
        return new MeasuredPayload(page.Items.Count, page.Total, firstTwenty, bytes);
    }

    private async Task<BenchmarkSample> MeasureAsync(string variant, string phase, int sequence, Func<CancellationToken, Task<MeasuredPayload>> action, CancellationToken ct)
    {
        commandCounter.Reset();
        var stopwatch = Stopwatch.StartNew();
        var payload = await action(ct);
        stopwatch.Stop();
        return new BenchmarkSample(variant, phase, sequence, stopwatch.Elapsed.TotalMilliseconds, payload.MaterializedRows, payload.PayloadUtf8Bytes, commandCounter.Value);
    }

    private IQueryable<AssetRow> AssetRows(ParkDbContext db) => db.Assets
        .AsNoTracking()
        .Include(x => x.Zone)
        .Include(x => x.Device)
        .Include(x => x.Plant)
        .Include(x => x.Facility)
        .Where(x => x.Category == BenchmarkCategory)
        .OrderBy(x => x.Code)
        .Select(x => new AssetRow(
            x.Id,
            x.Code,
            x.Name,
            x.Category,
            x.ZoneId,
            x.Zone == null ? null : x.Zone.Name,
            x.Longitude,
            x.Latitude,
            x.Status,
            x.PublicDescription,
            x.PublicCode,
            x.Version,
            x.Device == null ? null : new DeviceRow(x.Device.Id, x.Device.Code, x.Device.Type, x.Device.Enabled, x.Device.Model, x.Device.Manufacturer),
            x.Plant == null ? null : new PlantRow(x.Plant.Species, x.Plant.DiameterCm, x.Plant.HeightM, x.Plant.Health),
            x.Facility == null ? null : new FacilityRow(x.Facility.Kind, x.Facility.Specification, x.Facility.Quantity)));

    private sealed record MeasuredPayload(int MaterializedRows, int TotalCount, IReadOnlyList<AssetRow> FirstTwenty, int PayloadUtf8Bytes);
    private sealed record AssetRow(Guid Id, string Code, string Name, string Category, Guid? ZoneId, string? ZoneName, decimal? Longitude, decimal? Latitude, string Status, string PublicDescription, string PublicCode, int Version, DeviceRow? Device, PlantRow? Plant, FacilityRow? Facility);
    private sealed record DeviceRow(Guid Id, string Code, string Type, bool Enabled, string? Model, string? Manufacturer);
    private sealed record PlantRow(string Species, decimal? DiameterCm, decimal? HeightM, string Health);
    private sealed record FacilityRow(string Kind, string? Specification, int Quantity);
}
