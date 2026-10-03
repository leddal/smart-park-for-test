using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Assets;

namespace SmartPark.Api.Features.Overview;

public sealed record EcoIndexResult(bool Sufficient, decimal? Score, decimal? AirScore, decimal? NoiseScore, decimal? WaterScore, DateTimeOffset? GeneratedAt, string Formula, string? Source = null);
public static class EcoIndexService
{
    public static decimal Clamp(decimal value, decimal low = 0, decimal high = 100) => Math.Min(high, Math.Max(low, value));
    public static EcoIndexResult Calculate(decimal? pm25, decimal? noise, decimal? dissolvedOxygen, DateTimeOffset? generatedAt)
    {
        const string formula = "A=clamp(100×(1-PM2.5/150)); N=clamp(100×(1-max(dB-35,0)/50)); W=clamp(100×DO/8); E=0.4A+0.3N+0.3W";
        if (pm25 is null || noise is null || dissolvedOxygen is null) return new EcoIndexResult(false, null, null, null, null, generatedAt, formula);
        var air = Clamp(100m * (1m - pm25.Value / 150m)); var sound = Clamp(100m * (1m - Math.Max(noise.Value - 35m, 0m) / 50m)); var water = Clamp(100m * dissolvedOxygen.Value / 8m);
        return new EcoIndexResult(true, Math.Round(.4m * air + .3m * sound + .3m * water, 1), Math.Round(air, 1), Math.Round(sound, 1), Math.Round(water, 1), generatedAt, formula);
    }
    public static async Task<EcoIndexResult> GetAsync(ParkDbContext db, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        var latest = await db.TelemetrySamples.AsNoTracking()
            .Where(x => x.CollectedAt >= since && (x.MetricCode == "pm25" || x.MetricCode == "noise" || x.MetricCode == "dissolvedOxygen"))
            .Where(x => x.Id == db.TelemetrySamples
                .Where(candidate => candidate.DeviceId == x.DeviceId && candidate.MetricCode == x.MetricCode)
                .OrderByDescending(candidate => candidate.CollectedAt).ThenByDescending(candidate => candidate.Id)
                .Select(candidate => candidate.Id).First())
            .ToListAsync(ct);
        decimal? Average(string code)
        {
            var values = latest.Where(x => x.MetricCode == code).Select(x => x.Value).ToList();
            return values.Count == 0 ? null : values.Average();
        }
        DateTimeOffset? generatedAt = latest.Count == 0 ? null : latest.Max(x => x.CollectedAt);
        var source = latest.Select(x => x.Source).Distinct().ToList() switch { [] => null, [var single] => single, _ => "Mixed" };
        var result = Calculate(Average("pm25"), Average("noise"), Average("dissolvedOxygen"), generatedAt);
        return result with { Source = source };
    }
}

public sealed record VisitorOverview(int Inside, int TodayIn, int TodayOut, string Source);
public sealed record DeviceOverview(int Enabled, int Disabled, int Fresh, int Stale);
public sealed record CountOverview(int Active, int Open, int PendingReview, int Completed);
public sealed record AssetCategoryCount(string Category, int Count);
public sealed record MetricLatest(Guid DeviceId, string DeviceName, string MetricCode, decimal Value, string Unit, DateTimeOffset CollectedAt, string Source, bool Stale);
public sealed record CarbonOverview(decimal TotalKgCo2e, int ValidCount, int MissingCount);
public sealed record OverviewSnapshot(string ParkName, DateTimeOffset GeneratedAt, bool SimulationEnabled, VisitorOverview Visitors, DeviceOverview Devices, int Alerts, int Events, int WorkOrders, CountOverview WorkOrderSummary, IReadOnlyList<AssetCategoryCount> AssetCategories, IReadOnlyList<MetricLatest> Metrics, EcoIndexResult Eco, CarbonOverview Carbon, string CacheStatus);

public sealed class OverviewQueryService(ParkDbContext db)
{
    public async Task<OverviewSnapshot> GetAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var parkState = await db.Parks.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => new { x.Name, x.VisitorInsideCount }).FirstOrDefaultAsync(ct);
        var park = parkState?.Name ?? "智慧公园";
        var simulation = await db.SimulationStates.AsNoTracking().Select(x => x.Enabled).FirstOrDefaultAsync(ct);
        var zone = ChinaTimeZone(); var local = TimeZoneInfo.ConvertTime(now, zone); var startLocal = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset); var endLocal = startLocal.AddDays(1); var startUtc = startLocal.ToUniversalTime(); var endUtc = endLocal.ToUniversalTime();
        var counters = await db.VisitorCounterSamples.AsNoTracking().Where(x => x.CollectedAt >= startUtc && x.CollectedAt < endUtc).ToListAsync(ct);
        var todayIn = counters.Where(x => x.Direction == "In").Sum(x => x.Count); var todayOut = counters.Where(x => x.Direction == "Out").Sum(x => x.Count);
        var visitorSource = await db.VisitorCounterSamples.AsNoTracking().OrderByDescending(x => x.CollectedAt).Select(x => x.Source).FirstOrDefaultAsync(ct) ?? "Seed";
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct); var fresh = devices.Count(x => x.Enabled && x.LastTelemetryAt >= now.AddMinutes(-10));
        var categories = await db.Assets.AsNoTracking().GroupBy(x => x.Category).OrderBy(x => x.Key).Select(x => new AssetCategoryCount(x.Key, x.Count())).ToListAsync(ct);
        var latest = await db.TelemetrySamples.AsNoTracking()
            .Where(x => x.Id == db.TelemetrySamples
                .Where(candidate => candidate.DeviceId == x.DeviceId && candidate.MetricCode == x.MetricCode)
                .OrderByDescending(candidate => candidate.CollectedAt).ThenByDescending(candidate => candidate.Id)
                .Select(candidate => candidate.Id).First())
            .OrderBy(x => x.DeviceId).ThenBy(x => x.Device!.Asset!.Name).ThenBy(x => x.MetricCode)
            .Select(x => new MetricLatest(x.DeviceId, x.Device!.Asset!.Name, x.MetricCode, x.Value, x.Unit, x.CollectedAt, x.Source, x.CollectedAt < now.AddMinutes(-10)))
            .ToListAsync(ct);
        var orders = await db.WorkOrders.AsNoTracking().GroupBy(x => x.Status).Select(x => new { x.Key, Count = x.Count() }).ToListAsync(ct); var plants = await db.PlantProfiles.AsNoTracking().ToListAsync(ct); var validCarbon = plants.Where(x => CarbonEstimateService.TryEstimate(x.DiameterCm, x.HeightM, out _)).Select(x => CarbonEstimateService.Estimate(x.DiameterCm!.Value, x.HeightM!.Value)).ToList();
        var workOrderSummary = new CountOverview(orders.Sum(x => x.Count), orders.Where(x => x.Key is "Assigned" or "Accepted" or "InProgress").Sum(x => x.Count), orders.Where(x => x.Key == "PendingReview").Sum(x => x.Count), orders.Where(x => x.Key == "Completed").Sum(x => x.Count));
        return new OverviewSnapshot(park, now, simulation, new VisitorOverview(Math.Max(0, parkState?.VisitorInsideCount ?? 0), todayIn, todayOut, visitorSource), new DeviceOverview(devices.Count(x => x.Enabled), devices.Count(x => !x.Enabled), fresh, devices.Count(x => x.Enabled) - fresh), await db.Alerts.CountAsync(x => x.Status == "Active", ct), await db.ParkEvents.CountAsync(x => x.Status != "Closed" && x.Status != "FalseAlarm", ct), workOrderSummary.Open, workOrderSummary, categories, latest, await EcoIndexService.GetAsync(db, ct), new CarbonOverview(Math.Round(validCarbon.Sum(), 2), validCarbon.Count, plants.Count - validCarbon.Count), "Database");
    }
    private static TimeZoneInfo ChinaTimeZone() { try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); } }
}

public sealed class OverviewCacheService(OverviewQueryService query, ParkDbContext db, IDistributedCache distributed, IMemoryCache memory, ILogger<OverviewCacheService> logger)
{
    // Kept as a stable prefix for benchmark tooling; cache entries are namespaced by database below.
    private const string Key = "overview:v1";
    private readonly string cacheKey = $"{Key}:{db.Database.GetDbConnection().Database}";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Status { get; private set; } = "Unknown";
    public async Task<OverviewSnapshot> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var cached = await distributed.GetStringAsync(cacheKey, ct);
            if (!string.IsNullOrWhiteSpace(cached) && JsonSerializer.Deserialize<OverviewSnapshot>(cached, Json) is { } value) { Status = "Redis"; return value with { CacheStatus = "Redis" }; }
            var generated = await query.GetAsync(ct);
            await distributed.SetStringAsync(cacheKey, JsonSerializer.Serialize(generated, Json), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10) }, ct);
            Status = "Redis";
            return generated with { CacheStatus = "Redis" };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis overview cache unavailable; serving database fallback.");
            if (memory.TryGetValue<OverviewSnapshot>(cacheKey, out var memoryValue) && memoryValue is not null) { Status = "MemoryFallback"; return memoryValue with { CacheStatus = "MemoryFallback" }; }
            var generated = await query.GetAsync(ct);
            memory.Set(cacheKey, generated, TimeSpan.FromSeconds(10));
            Status = "MemoryFallback";
            return generated with { CacheStatus = "MemoryFallback" };
        }
    }
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        memory.Remove(cacheKey);
        try { await distributed.RemoveAsync(cacheKey, ct); }
        catch (Exception ex) { Status = "MemoryFallback"; logger.LogWarning(ex, "Redis cache invalidation failed."); }
    }
}
