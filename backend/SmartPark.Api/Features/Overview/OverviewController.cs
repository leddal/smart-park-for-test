using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;

namespace SmartPark.Api.Features.Overview;

[ApiController]
[Route("api/overview")]
[Authorize(Policy = Policies.Manager)]
public sealed class OverviewController(OverviewCacheService cache, ParkDbContext db) : ControllerBase
{
    [HttpGet("summary")]
    public Task<OverviewSnapshot> Summary(CancellationToken ct) => cache.GetAsync(ct);
    [HttpGet("trends")]
    public async Task<IReadOnlyList<object>> Trends(string metricCode, int hours = 24, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metricCode)) throw new ApiException("Validation failed", 400, "metricCode is required.");
        hours = Math.Clamp(hours, 1, 168);
        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        var hourly = await db.TelemetrySamples.AsNoTracking()
            .Where(x => x.MetricCode == metricCode && x.CollectedAt >= since)
            .GroupBy(x => new { x.CollectedAt.Year, x.CollectedAt.Month, x.CollectedAt.Day, x.CollectedAt.Hour })
            .Select(x => new { x.Key.Year, x.Key.Month, x.Key.Day, x.Key.Hour, Value = x.Average(v => v.Value) })
            .OrderBy(x => x.Year).ThenBy(x => x.Month).ThenBy(x => x.Day).ThenBy(x => x.Hour)
            .Take(168)
            .ToListAsync(ct);
        return hourly.Select(x => (object)new { time = new DateTimeOffset(x.Year, x.Month, x.Day, x.Hour, 0, 0, TimeSpan.Zero), value = Math.Round(x.Value, 3) }).ToList();
    }
}

[ApiController]
[Route("api/health")]
public sealed class HealthController(ParkDbContext db, OverviewCacheService cache) : ControllerBase
{
    [AllowAnonymous, HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "live" });
    [AllowAnonymous, HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        if (!await db.Database.CanConnectAsync(ct)) return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable", detail: "The API requires PostgreSQL for readiness.");
        await cache.GetAsync(ct);
        return Ok(new { status = "ready", database = "healthy", cache = cache.Status == "Redis" ? "healthy" : "degraded" });
    }
}
