using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Background;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Overview;
using SmartPark.Api.Storage;

namespace SmartPark.Api.Features.IoT;

public sealed record TelemetryIngestRequest(Guid DeviceId, string MetricCode, decimal Value, string Unit, DateTimeOffset CollectedAt, string? IdempotencyKey);
public sealed record TelemetryIngestResult(TelemetrySample Sample, bool AppliedToLiveState, Guid? AlertId, Guid? EventId);
public sealed record VisitorCountRequest(string Direction, int Count, DateTimeOffset CollectedAt, string? IdempotencyKey);

public sealed class TelemetryIngestService(ParkDbContext db, OverviewCacheService cache)
{
    public async Task<TelemetryIngestResult> IngestAsync(TelemetryIngestRequest request, string source, CancellationToken ct = default)
    {
        if (source is not ("Seed" or "Simulation" or "Manual")) throw new ApiException("Validation failed", 400, "Telemetry source is invalid.");
        if (request.CollectedAt > DateTimeOffset.UtcNow.AddMinutes(5)) throw new ApiException("Validation failed", 400, "Collected time is too far in the future.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Devices\" SET \"LastTelemetryAt\" = \"LastTelemetryAt\" WHERE \"Id\" = {request.DeviceId}", ct);
        var device = await db.Devices.Include(x => x.Asset).SingleOrDefaultAsync(x => x.Id == request.DeviceId, ct) ?? throw new ApiException("Device not found", 404);
        if (!device.Enabled || device.Asset?.Status != "Active") throw new ApiException("Device unavailable", 409, "Disabled or retired devices cannot accept telemetry.");
        var metric = await db.MetricDefinitions.SingleOrDefaultAsync(x => x.Code == request.MetricCode, ct) ?? throw new ApiException("Metric not found", 400);
        if (metric.Unit != request.Unit || (metric.DeviceType != device.Type && !(metric.Code == "occupancy" && device.Type == "Camera"))) throw new ApiException("Validation failed", 400, "Metric does not match this device type or unit.");
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await db.TelemetrySamples.SingleOrDefaultAsync(x => x.DeviceId == request.DeviceId && x.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null) { await tx.CommitAsync(ct); return new TelemetryIngestResult(existing, false, null, null); }
        }

        var latest = await db.TelemetrySamples.Where(x => x.DeviceId == request.DeviceId && x.MetricCode == request.MetricCode).OrderByDescending(x => x.CollectedAt).FirstOrDefaultAsync(ct);
        var sample = new TelemetrySample { DeviceId = request.DeviceId, MetricCode = request.MetricCode, Value = request.Value, Unit = request.Unit, CollectedAt = request.CollectedAt, Source = source, IdempotencyKey = request.IdempotencyKey };
        db.TelemetrySamples.Add(sample);
        if (latest is not null && request.CollectedAt <= latest.CollectedAt)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new TelemetryIngestResult(sample, false, null, null);
        }

        if (device.LastTelemetryAt is null || request.CollectedAt > device.LastTelemetryAt.Value) device.LastTelemetryAt = request.CollectedAt;
        Guid? newAlertId = null; Guid? newEventId = null;
        var rules = await db.AlertRules
            .Where(x => x.MetricCode == request.MetricCode && (x.DeviceId == request.DeviceId || (x.DeviceId == null && x.DeviceType == device.Type)) &&
                (x.Enabled || db.Alerts.Any(a => a.DeviceId == request.DeviceId && a.RuleId == x.Id && a.Status == "Active")))
            .ToListAsync(ct);
        foreach (var rule in rules)
        {
            var active = await db.Alerts.SingleOrDefaultAsync(x => x.DeviceId == request.DeviceId && x.RuleId == rule.Id && x.Status == "Active", ct);
            var abnormal = (rule.Lower is not null && request.Value < rule.Lower) || (rule.Upper is not null && request.Value > rule.Upper);
            if (abnormal && active is null && rule.Enabled)
            {
                var alert = new Alert { DeviceId = request.DeviceId, RuleId = rule.Id, MetricCode = request.MetricCode, Value = request.Value, Severity = rule.Severity, Status = "Active", TriggeredAt = request.CollectedAt };
                var evt = new ParkEvent { Number = $"EVT-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}", Title = $"{device.Code} {request.MetricCode} 阈值告警", Category = "Alert", Severity = rule.Severity, AssetId = device.AssetId, AlertId = alert.Id, Description = $"{request.MetricCode}={request.Value}{request.Unit}", Status = "Open", Longitude = device.Asset?.Longitude, Latitude = device.Asset?.Latitude };
                alert.EventId = evt.Id; db.AddRange(alert, evt, new AlertLog { AlertId = alert.Id, Message = "Threshold exceeded", OccurredAt = request.CollectedAt }, new EventLog { EventId = evt.Id, Action = "CreatedFromAlert", Text = $"Alert {alert.Id}" }, new OutboxMessage { Kind = "EventCreated", PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { eventId = evt.Id, alertId = alert.Id }) });
                newAlertId = alert.Id; newEventId = evt.Id;
            }
            else if (!abnormal && active is not null && IsRecovered(request.Value, rule))
            {
                active.Status = "Recovered"; active.RecoveredAt = request.CollectedAt; db.AlertLogs.Add(new AlertLog { AlertId = active.Id, Message = "Metric returned to recovery range", OccurredAt = request.CollectedAt });
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await cache.InvalidateAsync(ct);
        return new TelemetryIngestResult(sample, true, newAlertId, newEventId);
    }
    private static bool IsRecovered(decimal value, AlertRule rule)
    {
        var lower = rule.RecoveryLower ?? rule.Lower;
        var upper = rule.RecoveryUpper ?? rule.Upper;
        return (lower is null || value >= lower.Value) && (upper is null || value <= upper.Value);
    }
}

public sealed record VisitorCountIngestResult(VisitorCounterSample Sample, int Inside, bool Idempotent);
public sealed record VisitorCountSimulationBatchResult(VisitorCounterSample Inbound, VisitorCounterSample Outbound, int Inside, bool Idempotent);

public sealed class VisitorCountIngestService(ParkDbContext db, OverviewCacheService cache)
{
    public async Task<VisitorCountIngestResult> IngestAsync(VisitorCountRequest request, string source, CancellationToken ct = default)
    {
        if (source is not ("Seed" or "Simulation" or "Manual")) throw new ApiException("Validation failed", 400, "Visitor count source is invalid.");
        ValidateRequest(request);
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var parkId = await db.Parks.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct) ?? throw new ApiException("Park not initialized", 404);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Parks\" SET \"VisitorInsideCount\" = \"VisitorInsideCount\" WHERE \"Id\" = {parkId}", ct);
        var park = await db.Parks.SingleAsync(x => x.Id == parkId, ct);
        if (idempotencyKey is not null)
        {
            var existing = await db.VisitorCounterSamples.SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return new VisitorCountIngestResult(existing, park.VisitorInsideCount, true);
            }
        }

        await EnsureCollectedAtIsCurrentAsync(request.CollectedAt, ct);
        if (request.Direction == "Out" && request.Count > park.VisitorInsideCount) throw new ApiException("Visitor count conflict", 409, "Outbound count cannot exceed the current number inside the park.");

        var sample = new VisitorCounterSample { Direction = request.Direction, Count = request.Count, CollectedAt = request.CollectedAt, Source = source, Unit = "People", IdempotencyKey = idempotencyKey };
        park.VisitorInsideCount += request.Direction == "In" ? request.Count : -request.Count;
        db.VisitorCounterSamples.Add(sample);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await cache.InvalidateAsync(ct);
        return new VisitorCountIngestResult(sample, park.VisitorInsideCount, false);
    }

    public async Task<VisitorCountSimulationBatchResult> IngestSimulationBatchAsync(VisitorCountRequest inboundRequest, VisitorCountRequest outboundRequest, CancellationToken ct = default)
    {
        ValidateRequest(inboundRequest);
        ValidateRequest(outboundRequest);
        if (inboundRequest.Direction != "In" || outboundRequest.Direction != "Out") throw new ApiException("Validation failed", 400, "Simulation batches require an inbound and outbound count.");
        var inboundKey = NormalizeIdempotencyKey(inboundRequest.IdempotencyKey);
        var outboundKey = NormalizeIdempotencyKey(outboundRequest.IdempotencyKey);
        if (inboundKey is null || outboundKey is null || inboundKey == outboundKey) throw new ApiException("Validation failed", 400, "Simulation batches require distinct idempotency keys.");

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var parkId = await db.Parks.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct) ?? throw new ApiException("Park not initialized", 404);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Parks\" SET \"VisitorInsideCount\" = \"VisitorInsideCount\" WHERE \"Id\" = {parkId}", ct);
        var park = await db.Parks.SingleAsync(x => x.Id == parkId, ct);
        var existingInbound = await db.VisitorCounterSamples.SingleOrDefaultAsync(x => x.IdempotencyKey == inboundKey, ct);
        var existingOutbound = await db.VisitorCounterSamples.SingleOrDefaultAsync(x => x.IdempotencyKey == outboundKey, ct);
        if (existingInbound is not null || existingOutbound is not null)
        {
            if (existingInbound is null || existingOutbound is null) throw new ApiException("Idempotency conflict", 409, "Simulation batch idempotency keys do not refer to the same batch.");
            if (existingInbound.Source != "Simulation" || existingOutbound.Source != "Simulation" || existingInbound.Direction != "In" || existingOutbound.Direction != "Out" || existingInbound.CollectedAt != existingOutbound.CollectedAt) throw new ApiException("Idempotency conflict", 409, "Simulation batch idempotency keys do not refer to a valid simulation batch.");
            await tx.CommitAsync(ct);
            return new VisitorCountSimulationBatchResult(existingInbound, existingOutbound, park.VisitorInsideCount, true);
        }

        var collectedAt = inboundRequest.CollectedAt >= outboundRequest.CollectedAt ? inboundRequest.CollectedAt : outboundRequest.CollectedAt;
        var latestAt = await LatestVisitorCollectedAtAsync(ct);
        if (latestAt is not null && collectedAt < latestAt.Value) collectedAt = latestAt.Value;
        var outboundCount = Math.Min(outboundRequest.Count, park.VisitorInsideCount + inboundRequest.Count);
        var inbound = new VisitorCounterSample { Direction = "In", Count = inboundRequest.Count, CollectedAt = collectedAt, Source = "Simulation", Unit = "People", IdempotencyKey = inboundKey };
        var outbound = new VisitorCounterSample { Direction = "Out", Count = outboundCount, CollectedAt = collectedAt, Source = "Simulation", Unit = "People", IdempotencyKey = outboundKey };
        park.VisitorInsideCount += inbound.Count - outbound.Count;
        db.VisitorCounterSamples.AddRange(inbound, outbound);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await cache.InvalidateAsync(ct);
        return new VisitorCountSimulationBatchResult(inbound, outbound, park.VisitorInsideCount, false);
    }

    private async Task EnsureCollectedAtIsCurrentAsync(DateTimeOffset collectedAt, CancellationToken ct)
    {
        var latestAt = await LatestVisitorCollectedAtAsync(ct);
        if (latestAt is not null && collectedAt < latestAt.Value) throw new ApiException("Validation failed", 400, "Collected time cannot precede the latest counter sample.");
    }

    private Task<DateTimeOffset?> LatestVisitorCollectedAtAsync(CancellationToken ct) => db.VisitorCounterSamples.AsNoTracking().OrderByDescending(x => x.CollectedAt).Select(x => (DateTimeOffset?)x.CollectedAt).FirstOrDefaultAsync(ct);

    private static void ValidateRequest(VisitorCountRequest request)
    {
        if (request.Direction is not ("In" or "Out") || request.Count is < 1 or > 10_000) throw new ApiException("Validation failed", 400, "Direction must be In or Out and count must be between 1 and 10000.");
        if (request.CollectedAt > DateTimeOffset.UtcNow.AddMinutes(5)) throw new ApiException("Validation failed", 400, "Collected time is too far in the future.");
    }

    private static string? NormalizeIdempotencyKey(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

[ApiController]
[Route("api/iot")]
[Authorize(Policy = Policies.Manager)]
public sealed class IoTController(ParkDbContext db, TelemetryIngestService ingest, VisitorCountIngestService visitorCounts, LocalFileStore files, OverviewCacheService cache, AuditService audit, SimulationGate gate, IConfiguration configuration) : ControllerBase
{
    [HttpGet("devices")]
    public async Task<PageResult<object>> Devices(string? type, int page = 1, int pageSize = 20, CancellationToken ct = default) { var q = db.Devices.AsNoTracking().Include(x => x.Asset).AsQueryable(); if (!string.IsNullOrWhiteSpace(type)) q = q.Where(x => x.Type == type); var p = await q.OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Type, x.Enabled, x.Model, x.Manufacturer, x.AssetId, assetName = x.Asset!.Name, x.LastTelemetryAt, x.ControlState }).ToPageAsync(page, pageSize, ct); return new PageResult<object>(p.Items.Cast<object>().ToList(), p.Total, p.Page, p.PageSize); }
    [HttpGet("metrics")]
    public async Task<PageResult<MetricDefinition>> Metrics(int page = 1, int pageSize = 100, CancellationToken ct = default) => await db.MetricDefinitions.AsNoTracking().OrderBy(x => x.Code).ToPageAsync(page, pageSize, ct);
    [Authorize(Policy = Policies.Manager), HttpPost("telemetry")]
    public async Task<IActionResult> Telemetry(TelemetryIngestRequest request, CancellationToken ct) { var result = await ingest.IngestAsync(request, "Manual", ct); await audit.WriteAsync("ManualIngest", "Telemetry", result.Sample.Id, new { request.DeviceId, request.MetricCode }, ct); return Created($"/api/iot/telemetry/{result.Sample.Id}", new { result.Sample.Id, result.AppliedToLiveState, result.AlertId, result.EventId }); }
    [HttpPost("visitor-counts")]
    public async Task<IActionResult> RecordVisitorCount(VisitorCountRequest request, CancellationToken ct)
    {
        var result = await visitorCounts.IngestAsync(request, "Manual", ct);
        if (!result.Idempotent)
            await audit.WriteAsync("ManualIngest", "VisitorCounter", result.Sample.Id, new { result.Sample.Direction, result.Sample.Count }, ct);
        var response = new { result.Sample.Id, result.Sample.Direction, result.Sample.Count, result.Sample.CollectedAt, result.Sample.Source, result.Sample.Unit, inside = result.Inside, idempotent = result.Idempotent };
        return result.Idempotent ? Ok(response) : Created($"/api/iot/visitor-counts/{result.Sample.Id}", response);
    }
    [HttpGet("latest")]
    public async Task<IReadOnlyList<object>> Latest(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var newest = await db.TelemetrySamples.AsNoTracking()
            .Where(x => x.Id == db.TelemetrySamples
                .Where(candidate => candidate.DeviceId == x.DeviceId && candidate.MetricCode == x.MetricCode)
                .OrderByDescending(candidate => candidate.CollectedAt).ThenByDescending(candidate => candidate.Id)
                .Select(candidate => candidate.Id).First())
            .Select(x => new { x.DeviceId, DeviceName = x.Device!.Asset!.Name, x.MetricCode, x.Value, x.Unit, x.CollectedAt, x.Source })
            .OrderBy(x => x.DeviceId).ThenBy(x => x.DeviceName).ThenBy(x => x.MetricCode)
            .ToListAsync(ct);
        return newest.Select(x => (object)new { x.DeviceId, x.DeviceName, x.MetricCode, x.Value, x.Unit, x.CollectedAt, x.Source, stale = x.CollectedAt < now.AddMinutes(-10) }).ToList();
    }
    [HttpGet("trends")]
    public async Task<IReadOnlyList<object>> Trends(Guid deviceId, string metricCode, int hours = 24, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metricCode)) throw new ApiException("Validation failed", 400, "metricCode is required.");
        hours = Math.Clamp(hours, 1, 168);
        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        var hourly = await db.TelemetrySamples.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.MetricCode == metricCode && x.CollectedAt >= since)
            .GroupBy(x => new { x.CollectedAt.Year, x.CollectedAt.Month, x.CollectedAt.Day, x.CollectedAt.Hour })
            .Select(x => new { x.Key.Year, x.Key.Month, x.Key.Day, x.Key.Hour, Value = x.Average(v => v.Value) })
            .OrderBy(x => x.Year).ThenBy(x => x.Month).ThenBy(x => x.Day).ThenBy(x => x.Hour)
            .Take(168)
            .ToListAsync(ct);
        return hourly.Select(x => (object)new { time = new DateTimeOffset(x.Year, x.Month, x.Day, x.Hour, 0, 0, TimeSpan.Zero), value = Math.Round(x.Value, 3) }).ToList();
    }
    [HttpGet("rules")]
    public async Task<PageResult<AlertRule>> Rules(int page = 1, int pageSize = 20, CancellationToken ct = default) => await db.AlertRules.AsNoTracking().OrderBy(x => x.MetricCode).ToPageAsync(page, pageSize, ct);
    [Authorize(Policy = Policies.Administrator), HttpPost("rules")]
    public async Task<IActionResult> CreateRule(AlertRuleRequest request, CancellationToken ct)
    {
        await ValidateRuleAsync(request, ct);
        var rule = MapRule(new AlertRule(), request);
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(ct);
        await audit.WriteAsync("Create", "AlertRule", rule.Id, new { rule.MetricCode, rule.DeviceId, rule.DeviceType, rule.Enabled }, ct);
        return Created($"/api/iot/rules/{rule.Id}", rule);
    }
    [Authorize(Policy = Policies.Administrator), HttpPut("rules/{id:guid}")]
    public async Task<AlertRule> UpdateRule(Guid id, AlertRuleRequest request, CancellationToken ct)
    {
        await ValidateRuleAsync(request, ct);
        var rule = await db.AlertRules.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Rule not found", 404);
        if (rule.Version != request.Version) throw new ApiException("Version conflict", 409);
        MapRule(rule, request);
        rule.Version++;
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(ct);
        await audit.WriteAsync("Update", "AlertRule", id, new { rule.MetricCode, rule.DeviceId, rule.DeviceType, rule.Enabled }, ct);
        return rule;
    }
    [HttpGet("alerts")]
    public async Task<PageResult<object>> Alerts(string? status, int page = 1, int pageSize = 20, CancellationToken ct = default) { var q = db.Alerts.AsNoTracking().AsQueryable(); if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status); var p = await q.OrderByDescending(x => x.TriggeredAt).Select(x => new { x.Id, x.DeviceId, x.RuleId, x.EventId, x.MetricCode, x.Value, x.Severity, x.Status, x.TriggeredAt, x.AcknowledgedAt, x.RecoveredAt }).ToPageAsync(page, pageSize, ct); return new PageResult<object>(p.Items.Cast<object>().ToList(), p.Total, p.Page, p.PageSize); }
    [HttpGet("alerts/{id:guid}")]
    public async Task<object> Alert(Guid id, CancellationToken ct) { var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Alert not found", 404); return new { alert, logs = await db.AlertLogs.AsNoTracking().Where(x => x.AlertId == id).OrderBy(x => x.OccurredAt).ToListAsync(ct) }; }
    [HttpPost("alerts/{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct) { var alert = await db.Alerts.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Alert not found", 404); if (alert.AcknowledgedAt is null) { alert.AcknowledgedAt = DateTimeOffset.UtcNow; alert.AcknowledgedById = User.Id(); db.AlertLogs.Add(new AlertLog { AlertId = id, Message = "Acknowledged" }); await db.SaveChangesAsync(ct); } return NoContent(); }
    [HttpGet("pests")]
    public async Task<PageResult<PestObservation>> Pests(int page = 1, int pageSize = 20, CancellationToken ct = default) => await db.PestObservations.AsNoTracking().OrderByDescending(x => x.ObservedAt).ToPageAsync(page, pageSize, ct);
    [Authorize(Policy = Policies.Manager), HttpPost("pests")]
    public async Task<IActionResult> CreatePest(PestRequest request, CancellationToken ct) { if (request.Count < 0 || string.IsNullOrWhiteSpace(request.Species)) throw new ApiException("Validation failed", 400); var item = new PestObservation { DeviceId = request.DeviceId, Species = request.Species.Trim(), Count = request.Count, ObservedAt = request.ObservedAt ?? DateTimeOffset.UtcNow, Notes = request.Notes, Source = "Manual" }; db.PestObservations.Add(item); await db.SaveChangesAsync(ct); return Created($"/api/iot/pests/{item.Id}", item); }
    [HttpGet("simulation")]
    public async Task<object> Simulation(CancellationToken ct) => new { enabled = (await db.SimulationStates.AsNoTracking().OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct))?.Enabled ?? false };
    [Authorize(Policy = Policies.Administrator), HttpPost("simulation")]
    public async Task<object> SetSimulation(SimulationRequest request, CancellationToken ct)
    {
        using var lease = await gate.AcquireAsync(ct);
        EnsureDemoEnabled();
        var item = await db.SimulationStates.FirstOrDefaultAsync(ct);
        if (item is null) { item = new SimulationState(); db.SimulationStates.Add(item); }
        item.Enabled = request.Enabled;
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(ct);
        await audit.WriteAsync("SetSimulation", "Simulation", item.Id, new { item.Enabled }, ct);
        return new { enabled = item.Enabled };
    }
    [Authorize(Policy = Policies.Administrator), HttpPost("scenarios")]
    public async Task<object> Scenario(ScenarioRequest request, CancellationToken ct)
    {
        EnsureDemoEnabled();
        if (request.Scenario is not ("Abnormal" or "Recovery")) throw new ApiException("Validation failed", 400);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.DeviceId, ct) ?? throw new ApiException("Device not found", 404);
        var rule = await db.AlertRules.AsNoTracking().FirstOrDefaultAsync(x => (x.DeviceId == request.DeviceId || x.DeviceId == null && x.DeviceType == device.Type) && x.MetricCode == request.MetricCode && x.Enabled, ct) ?? throw new ApiException("Rule not found", 404);
        var value = request.Scenario == "Abnormal" ? rule.Upper is not null ? rule.Upper.Value + 1 : rule.Lower!.Value - 1 : rule.RecoveryUpper ?? rule.RecoveryLower ?? rule.Upper ?? rule.Lower ?? 0;
        var result = await ingest.IngestAsync(new TelemetryIngestRequest(request.DeviceId, request.MetricCode, value, rule.Unit, DateTimeOffset.UtcNow, $"scenario-{Guid.NewGuid():N}"), "Simulation", ct);
        return new { result.Sample.Id, result.AlertId, result.EventId, result.AppliedToLiveState };
    }
    [HttpGet("media")]
    public async Task<IReadOnlyList<MediaMaterial>> Media(CancellationToken ct) => await db.MediaMaterials.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    [Authorize(Policy = Policies.Manager), HttpPost("media")]
    public async Task<IActionResult> UploadAudio([FromForm] IFormFile file, CancellationToken ct) { var stored = await files.SaveAsync(file, "audio", User.Id(), false, false, ct); var media = new MediaMaterial { Name = Path.GetFileNameWithoutExtension(stored.OriginalName), FileId = stored.Id, ContentType = stored.ContentType }; db.MediaMaterials.Add(media); await db.SaveChangesAsync(ct); return Created($"/api/iot/media/{media.Id}", media); }
    private void EnsureDemoEnabled()
    {
        if (bool.TryParse(configuration["Demo:Enabled"], out var enabled) && !enabled)
            throw new ApiException("Demo disabled", 409, "Simulation and scenario APIs are disabled when Demo:Enabled is false.");
    }

    private static AlertRule MapRule(AlertRule x, AlertRuleRequest r) { x.DeviceId = r.DeviceId; x.DeviceType = r.DeviceType; x.MetricCode = r.MetricCode; x.Unit = r.Unit; x.Lower = r.Lower; x.Upper = r.Upper; x.RecoveryLower = r.RecoveryLower; x.RecoveryUpper = r.RecoveryUpper; x.Severity = r.Severity; x.Enabled = r.Enabled; return x; }
    private async Task ValidateRuleAsync(AlertRuleRequest r, CancellationToken ct)
    {
        if ((r.DeviceId is null) == string.IsNullOrWhiteSpace(r.DeviceType) || string.IsNullOrWhiteSpace(r.MetricCode) || r.Lower is null && r.Upper is null || r.Lower is not null && r.Upper is not null && r.Lower >= r.Upper || r.RecoveryLower is not null && r.RecoveryUpper is not null && r.RecoveryLower > r.RecoveryUpper)
            throw new ApiException("Validation failed", 400, "Rule bounds or target are invalid.");
        if (r.Lower is not null && r.RecoveryLower is not null && r.RecoveryLower < r.Lower)
            throw new ApiException("Validation failed", 400, "Recovery lower bound must be within the alarm clear range.");
        if (r.Upper is not null && r.RecoveryUpper is not null && r.RecoveryUpper > r.Upper)
            throw new ApiException("Validation failed", 400, "Recovery upper bound must be within the alarm clear range.");
        if (r.Upper is not null && r.RecoveryLower is not null && r.RecoveryLower > r.Upper || r.Lower is not null && r.RecoveryUpper is not null && r.RecoveryUpper < r.Lower)
            throw new ApiException("Validation failed", 400, "Recovery bounds must remain within the normal range.");
        if (r.Lower is null && r.RecoveryLower is not null || r.Upper is null && r.RecoveryUpper is not null)
            throw new ApiException("Validation failed", 400, "Recovery bounds require their corresponding alarm bounds.");
        var metric = await db.MetricDefinitions.SingleOrDefaultAsync(x => x.Code == r.MetricCode, ct) ?? throw new ApiException("Validation failed", 400, "Metric definition is not available.");
        if (metric.Unit != r.Unit) throw new ApiException("Validation failed", 400, "Rule unit must match the metric definition.");
        if (r.DeviceId is not null)
        {
            var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == r.DeviceId, ct) ?? throw new ApiException("Device not found", 404);
            if (metric.DeviceType != device.Type && !(metric.Code == "occupancy" && device.Type == "Camera")) throw new ApiException("Validation failed", 400, "Rule metric does not match the device type.");
        }
        else if (metric.DeviceType != r.DeviceType) throw new ApiException("Validation failed", 400, "Rule metric does not match the device type.");
    }
}

public sealed record AlertRuleRequest(Guid? DeviceId, string? DeviceType, string MetricCode, string Unit, decimal? Lower, decimal? Upper, decimal? RecoveryLower, decimal? RecoveryUpper, string Severity, bool Enabled, int Version);
public sealed record PestRequest(Guid? DeviceId, string Species, int Count, DateTimeOffset? ObservedAt, string? Notes);
public sealed record SimulationRequest(bool Enabled);
public sealed record ScenarioRequest(Guid DeviceId, string MetricCode, string Scenario);
