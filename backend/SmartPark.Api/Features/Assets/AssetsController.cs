using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Overview;

namespace SmartPark.Api.Features.Assets;

[ApiController]
[Route("api/assets")]
[Authorize(Policy = Policies.Internal)]
public sealed class AssetsController(ParkDbContext db, OverviewCacheService cache, AuditService audit) : ControllerBase
{
    [Authorize(Policy = Policies.Manager), HttpGet]
    public async Task<PageResult<object>> List(string? category, string? status, string? search, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var query = db.Assets.AsNoTracking().Include(x => x.Zone).Include(x => x.Device).Include(x => x.Plant).Include(x => x.Facility).AsQueryable();
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Code.Contains(search) || x.Name.Contains(search));
        var result = await query.OrderBy(x => x.Code).Select(x => ToRow(x)).ToPageAsync(page, pageSize, ct);
        return new PageResult<object>(result.Items.Cast<object>().ToList(), result.Total, result.Page, result.PageSize);
    }

    [Authorize(Policy = Policies.Manager), HttpGet("carbon")]
    public async Task<object> Carbon(CancellationToken ct)
    {
        var plants = await db.PlantProfiles.AsNoTracking().ToListAsync(ct);
        var valid = plants.Where(x => CarbonEstimateService.TryEstimate(x.DiameterCm, x.HeightM, out _)).Select(x => CarbonEstimateService.Estimate(x.DiameterCm!.Value, x.HeightM!.Value)).ToList();
        return new { name = "植物碳储量估算", totalKgCo2e = Math.Round(valid.Sum(), 2), validCount = valid.Count, missingCount = plants.Count - valid.Count, formula = "B=0.1×D²×H; C=B×0.5×44/12 (kg CO₂e，演示估算)" };
    }

    [HttpGet("{id:guid}")]
    public async Task<object> Detail(Guid id, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().Include(x => x.Zone).Include(x => x.Device).Include(x => x.Plant).Include(x => x.Facility).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Asset not found", 404);
        var workerOnly = User.IsInRole(ParkRoles.Worker) && !User.IsManager();
        var workOrderQuery = db.WorkOrders.AsNoTracking().Where(x => x.AssetId == id);
        if (workerOnly)
        {
            var userId = User.Id();
            workOrderQuery = workOrderQuery.Where(x => x.AssigneeId == userId);
            if (!await workOrderQuery.AnyAsync(ct)) throw new ApiException("Forbidden", 403, "Workers can view only assets associated with their own work orders.");
            var ownWorkOrders = await workOrderQuery.OrderByDescending(x => x.CreatedAt).Take(50).Select(x => new { x.Id, x.Number, x.Title, x.Status, x.Priority, x.DueAt }).ToListAsync(ct);
            return new { asset = ToRow(asset), workOrders = ownWorkOrders };
        }

        var workOrders = await workOrderQuery.OrderByDescending(x => x.CreatedAt).Take(50).Select(x => new { x.Id, x.Number, x.Title, x.Status, x.Priority, x.DueAt }).ToListAsync(ct);
        var events = await db.ParkEvents.AsNoTracking().Where(x => x.AssetId == id).OrderByDescending(x => x.CreatedAt).Take(50).Select(x => new { x.Id, x.Number, x.Title, x.Status, x.Severity }).ToListAsync(ct);
        return new { asset = ToRow(asset), workOrders, events, maintenance = await db.AssetMaintenanceHistories.AsNoTracking().Where(x => x.AssetId == id).OrderByDescending(x => x.CompletedAt).ToListAsync(ct) };
    }

    [Authorize(Policy = Policies.Administrator), HttpPost]
    public async Task<IActionResult> Create(AssetRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        if (await db.Assets.AnyAsync(x => x.Code == request.Code.Trim() || x.PublicCode == request.PublicCode.Trim(), ct)) throw new ApiException("Duplicate asset", 409, "Asset code and public code must be unique.");
        var asset = Build(new Asset(), request);
        db.Assets.Add(asset);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(ct);
        await audit.WriteAsync("Create", "Asset", asset.Id, new { asset.Code }, ct);
        return Created($"/api/assets/{asset.Id}", ToRow(asset));
    }

    [Authorize(Policy = Policies.Administrator), HttpPut("{id:guid}")]
    public async Task<object> Update(Guid id, AssetRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var asset = await db.Assets.Include(x => x.Device).Include(x => x.Plant).Include(x => x.Facility).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Asset not found", 404);
        if (asset.Version != request.Version) throw new ApiException("Version conflict", 409, "Refresh the asset before saving.");
        if (!string.Equals(asset.Code, request.Code.Trim(), StringComparison.Ordinal) || !string.Equals(asset.PublicCode, request.PublicCode.Trim(), StringComparison.Ordinal)) throw new ApiException("Validation failed", 400, "Asset code and public code are immutable after creation.");
        if (asset.Status == "Retired" && request.Status is not null && request.Status != "Retired") throw new ApiException("Validation failed", 400, "Retired assets cannot be re-enabled.");
        if (request.Category == "Sensor" && (asset.Plant is not null || asset.Facility is not null) || request.Category == "Plant" && (asset.Device is not null || asset.Facility is not null) || request.Category == "Facility" && asset.Plant is not null) throw new ApiException("Validation failed", 400, "Asset category conflicts with its existing profile.");
        Build(asset, request);
        asset.Version++;
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(ct);
        await audit.WriteAsync("Update", "Asset", id, new { asset.Code }, ct);
        return ToRow(asset);
    }

    [Authorize(Policy = Policies.Administrator), HttpPost("{id:guid}/retire")]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct)
    {
        var asset = await db.Assets.Include(x => x.Device).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Asset not found", 404);
        asset.Status = "Retired"; asset.Version++; if (asset.Device is not null) asset.Device.Enabled = false;
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(ct);
        await audit.WriteAsync("Retire", "Asset", id, null, ct);
        return NoContent();
    }

    private async Task ValidateAsync(AssetRequest r, CancellationToken ct)
    {
        if (r.Category is not ("Sensor" or "Facility" or "Plant")) throw new ApiException("Validation failed", 400, "Category must be Sensor, Facility or Plant.");
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.PublicCode)) throw new ApiException("Validation failed", 400, "Code, name and public code are required.");
        if (r.Status is not null and not ("Active" or "Retired")) throw new ApiException("Validation failed", 400, "Status must be Active or Retired.");
        if (r.Longitude is < -180 or > 180 || r.Latitude is < -90 or > 90) throw new ApiException("Validation failed", 400, "Coordinates are invalid.");
        if (r.ZoneId is not null && !await db.ParkZones.AsNoTracking().AnyAsync(x => x.Id == r.ZoneId, ct)) throw new ApiException("Validation failed", 400, "Zone does not exist.");
        if (r.Category == "Sensor" && (r.Plant is not null || r.Facility is not null) || r.Category == "Plant" && (r.Device is not null || r.Facility is not null) || r.Category == "Facility" && r.Plant is not null) throw new ApiException("Validation failed", 400, "Asset category and profile combination is invalid.");
        if (r.Device is not null)
        {
            if (string.IsNullOrWhiteSpace(r.Device.Code) || r.Device.Type is not ("Soil" or "WaterLevel" or "WaterQuality" or "Toilet" or "Noise" or "Weather" or "Pest" or "Camera" or "Irrigation" or "Lamp" or "Broadcast")) throw new ApiException("Validation failed", 400, "Device code or type is invalid.");
            if (r.Status == "Retired" && r.Device.Enabled) throw new ApiException("Validation failed", 400, "Retired devices cannot be enabled.");
        }
        if (r.Facility is { Quantity: < 1 }) throw new ApiException("Validation failed", 400, "Facility quantity must be positive.");
    }
    private static Asset Build(Asset asset, AssetRequest r)
    {
        asset.Code = r.Code.Trim(); asset.Name = r.Name.Trim(); asset.Category = r.Category; asset.ZoneId = r.ZoneId; asset.Longitude = r.Longitude; asset.Latitude = r.Latitude; asset.Status = r.Status ?? asset.Status; asset.PublicDescription = r.PublicDescription ?? ""; asset.PublicCode = r.PublicCode.Trim();
        if (r.Device is not null) { var device = asset.Device ??= new Device { AssetId = asset.Id }; device.Code = r.Device.Code.Trim(); device.Type = r.Device.Type; device.Enabled = asset.Status != "Retired" && r.Device.Enabled; device.Model = r.Device.Model; device.Manufacturer = r.Device.Manufacturer; }
        if (r.Plant is not null) { var plant = asset.Plant ??= new PlantProfile { AssetId = asset.Id }; plant.Species = r.Plant.Species; plant.DiameterCm = r.Plant.DiameterCm; plant.HeightM = r.Plant.HeightM; plant.Health = r.Plant.Health; }
        if (r.Facility is not null) { var facility = asset.Facility ??= new FacilityProfile { AssetId = asset.Id }; facility.Kind = r.Facility.Kind; facility.Specification = r.Facility.Specification; facility.Quantity = r.Facility.Quantity; }
        return asset;
    }
    internal static object ToRow(Asset x) => new { x.Id, x.Code, x.Name, x.Category, x.ZoneId, zoneName = x.Zone?.Name, x.Longitude, x.Latitude, x.Status, x.PublicDescription, x.PublicCode, x.Version, device = x.Device is null ? null : new { x.Device.Id, x.Device.Code, x.Device.Type, x.Device.Enabled, x.Device.Model, x.Device.Manufacturer }, plant = x.Plant is null ? null : new { x.Plant.Species, x.Plant.DiameterCm, x.Plant.HeightM, x.Plant.Health }, facility = x.Facility is null ? null : new { x.Facility.Kind, x.Facility.Specification, x.Facility.Quantity } };
}

[ApiController]
[Route("api/public/assets")]
public sealed class PublicAssetsController(ParkDbContext db) : ControllerBase
{
    [AllowAnonymous, HttpGet("{publicCode}")]
    public async Task<object> Get(string publicCode, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().Include(x => x.Zone).SingleOrDefaultAsync(x => x.PublicCode == publicCode && x.Status == "Active", ct) ?? throw new ApiException("Asset not found", 404);
        return new { asset.Id, asset.Name, asset.Category, zoneName = asset.Zone?.Name, asset.Longitude, asset.Latitude, asset.PublicDescription, asset.PublicCode };
    }
}

public sealed record AssetRequest(string Code, string Name, string Category, Guid? ZoneId, decimal? Longitude, decimal? Latitude, string? Status, string? PublicDescription, string PublicCode, int Version, DeviceRequest? Device, PlantRequest? Plant, FacilityRequest? Facility);
public sealed record DeviceRequest(string Code, string Type, bool Enabled, string? Model, string? Manufacturer);
public sealed record PlantRequest(string Species, decimal? DiameterCm, decimal? HeightM, string Health);
public sealed record FacilityRequest(string Kind, string? Specification, int Quantity);

public static class CarbonEstimateService
{
    public static decimal Estimate(decimal diameterCm, decimal heightM) => 0.1m * diameterCm * diameterCm * heightM * 0.5m * 44m / 12m;
    public static bool TryEstimate(decimal? diameterCm, decimal? heightM, out decimal co2e)
    {
        co2e = 0; if (diameterCm is not > 0 || heightM is not > 0) return false; co2e = Estimate(diameterCm.Value, heightM.Value); return true;
    }
}
