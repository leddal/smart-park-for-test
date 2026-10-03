using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Overview;

namespace SmartPark.Api.Features.Control;

[ApiController]
[Route("api/control")]
[Authorize(Policy = Policies.Manager)]
public sealed class ControlController(ParkDbContext db, OverviewCacheService cache, AuditService audit) : ControllerBase
{
    [HttpGet("devices")]
    public async Task<IReadOnlyList<object>> Devices(CancellationToken ct) => await db.Devices.AsNoTracking().Include(x => x.Asset).Where(x => x.Type == "Irrigation" || x.Type == "Lamp" || x.Type == "Broadcast").OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Type, x.Enabled, name = x.Asset!.Name, state = x.ControlState, assetName = x.Asset!.Name, assetStatus = x.Asset!.Status, x.ControlState, x.ControlSequence, x.ControlExpiresAt }).Cast<object>().ToListAsync(ct);
    [HttpGet("commands")]
    public async Task<PageResult<object>> Commands(int page = 1, int pageSize = 20, CancellationToken ct = default) { var p = await db.ControlCommands.AsNoTracking().OrderByDescending(x => x.CreatedAt).Select(x => new { x.Id, x.DeviceId, x.RequestedById, x.Action, x.DurationSeconds, x.Brightness, x.Text, x.Outcome, x.Status, x.Sequence, x.Receipt, x.CreatedAt, x.ProcessedAt }).ToPageAsync(page, pageSize, ct); return new PageResult<object>(p.Items.Cast<object>().ToList(), p.Total, p.Page, p.PageSize); }
    [Authorize(Policy = Policies.Manager), HttpPost("commands")]
    public async Task<IActionResult> Create(CommandRequest request, CancellationToken ct)
    {
        var device = await db.Devices.Include(x => x.Asset).SingleOrDefaultAsync(x => x.Id == request.DeviceId, ct) ?? throw new ApiException("Device not found", 404);
        if (!device.Enabled || device.Asset?.Status != "Active") throw new ApiException("Device unavailable", 409);
        if (device.Type is not ("Irrigation" or "Lamp" or "Broadcast")) throw new ApiException("Validation failed", 400, "The target is not controllable.");
        Validate(device.Type, request);
        if (device.Type == "Irrigation" && request.Action == "Start" && device.ControlState == "Running") return Ok(new { deviceId = device.Id, state = device.ControlState, message = "Irrigation is already running." });
        device.ControlSequence++;
        var command = new ControlCommand { DeviceId = device.Id, RequestedById = User.Id(), Action = request.Action, DurationSeconds = request.DurationSeconds, Brightness = request.Brightness, Text = request.Text, Outcome = request.Outcome ?? "Success", Status = "Pending", Sequence = device.ControlSequence };
        db.ControlCommands.Add(command); await db.SaveChangesAsync(ct); await cache.InvalidateAsync(ct); await audit.WriteAsync("Create", "ControlCommand", command.Id, new { device.Code, command.Action }, ct); return Accepted($"/api/control/commands/{command.Id}", new { command.Id, command.Status, command.Sequence, message = "Queued for local simulation; no hardware command is sent." });
    }
    private static void Validate(string type, CommandRequest request)
    {
        if (request.Outcome is not (null or "Success" or "Fail" or "Timeout")) throw new ApiException("Validation failed", 400, "Outcome must be Success, Fail or Timeout.");
        var valid = type switch { "Irrigation" => request.Action is "Start" or "Stop", "Lamp" => request.Action is "Start" or "Stop" or "Brightness", "Broadcast" => request.Action is "Broadcast" or "Stop", _ => false };
        if (!valid) throw new ApiException("Validation failed", 400, "Action is not valid for this device.");
        if (type == "Irrigation" && request.Action == "Start" && (request.DurationSeconds is null || request.DurationSeconds < 1 || request.DurationSeconds > 3600)) throw new ApiException("Validation failed", 400, "Irrigation duration must be 1 to 3600 seconds.");
        if (request.Action == "Brightness" && (request.Brightness is null || request.Brightness < 0 || request.Brightness > 100)) throw new ApiException("Validation failed", 400, "Brightness must be 0 to 100.");
        if (request.Action == "Broadcast" && string.IsNullOrWhiteSpace(request.Text)) throw new ApiException("Validation failed", 400, "Broadcast text is required.");
    }
}

public sealed record CommandRequest(Guid DeviceId, string Action, int? DurationSeconds, int? Brightness, string? Text, string? Outcome);
