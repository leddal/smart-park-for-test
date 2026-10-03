using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Overview;

namespace SmartPark.Api.Background;

public sealed class CommandHostedService(IServiceScopeFactory scopes, ILogger<CommandHostedService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>();
        var running = await db.Devices.Where(x => x.ControlState != "Stopped").ToListAsync(cancellationToken);
        foreach (var device in running) { device.ControlSequence++; device.ControlState = "Stopped"; device.ControlExpiresAt = null; db.ControlCommands.Add(new ControlCommand { DeviceId = device.Id, RequestedById = Guid.Empty, Action = "Stop", Outcome = "Success", Status = "SimulatedSucceeded", Sequence = device.ControlSequence, ProcessedAt = DateTimeOffset.UtcNow, Receipt = "Simulation state reset after process restart; no hardware state was inferred." }); }
        if (running.Count > 0) await db.SaveChangesAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Control command processing failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
    private async Task ProcessAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>(); var cache = scope.ServiceProvider.GetRequiredService<OverviewCacheService>();
        var now = DateTimeOffset.UtcNow;
        var expired = await db.Devices.Where(x => x.ControlState == "Running" && x.ControlExpiresAt != null && x.ControlExpiresAt <= now).ToListAsync(ct);
        foreach (var device in expired) { device.ControlSequence++; device.ControlState = "Stopped"; device.ControlExpiresAt = null; db.ControlCommands.Add(new ControlCommand { DeviceId = device.Id, RequestedById = Guid.Empty, Action = "Stop", Outcome = "Success", Status = "Pending", Sequence = device.ControlSequence, Receipt = "Automatic expiry stop applied locally and queued for durable receipt." }); }
        var commands = await db.ControlCommands.Include(x => x.Device).Where(x => x.Status == "Pending").OrderBy(x => x.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var command in commands)
        {
            command.ProcessedAt = now;
            if (command.Outcome == "Fail") { command.Status = "Failed"; command.Receipt = "Local simulation failed by requested scenario; no hardware command was sent."; continue; }
            if (command.Outcome == "Timeout") { command.Status = "TimedOut"; command.Receipt = "Local simulation timed out by requested scenario; no hardware command was sent."; continue; }
            command.Status = "SimulatedSucceeded"; command.Receipt = "Local simulation succeeded; no hardware command was sent.";
            var device = command.Device!;
            if (device.ControlSequence != command.Sequence) { command.Receipt = "Local simulation receipt ignored because a newer command superseded it."; continue; }
            device.ControlState = command.Action switch { "Start" => "Running", "Stop" => "Stopped", "Brightness" => $"Brightness:{command.Brightness}", "Broadcast" => "Broadcasting", _ => device.ControlState };
            device.ControlExpiresAt = command.Action == "Start" && command.DurationSeconds is not null ? now.AddSeconds(command.DurationSeconds.Value) : command.Action == "Stop" ? null : device.ControlExpiresAt;
        }
        if (expired.Count > 0 || commands.Count > 0) { await db.SaveChangesAsync(ct); await cache.InvalidateAsync(ct); }
    }
}
