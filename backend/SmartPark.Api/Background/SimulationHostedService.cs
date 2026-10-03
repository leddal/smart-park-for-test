using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Data;
using SmartPark.Api.Features.IoT;

namespace SmartPark.Api.Background;

public sealed class SimulationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try { return new Lease(_semaphore); }
        catch { _semaphore.Release(); throw; }
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}

public sealed class SimulationHostedService(IServiceScopeFactory scopes, SimulationGate gate, ILogger<SimulationHostedService> logger) : BackgroundService
{
    private readonly Random _random = new();
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var lease = await gate.AcquireAsync(cancellationToken);
            await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>(); var state = await db.SimulationStates.FirstOrDefaultAsync(cancellationToken); if (state is not null && state.Enabled) { state.Enabled = false; await db.SaveChangesAsync(cancellationToken); }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Simulation state could not be reset at startup"); }
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SimulateAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Telemetry simulation failed"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
    private async Task SimulateAsync(CancellationToken ct)
    {
        using var lease = await gate.AcquireAsync(ct);
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>(); var state = await db.SimulationStates.AsNoTracking().OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct); if (state?.Enabled != true) return;
        var ingest = scope.ServiceProvider.GetRequiredService<TelemetryIngestService>(); var visitorCounts = scope.ServiceProvider.GetRequiredService<VisitorCountIngestService>(); await SimulateVisitorCountsAsync(visitorCounts, ct); var definitions = await db.MetricDefinitions.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct); var devices = await db.Devices.AsNoTracking().Where(x => x.Enabled && db.MetricDefinitions.Any(metric => metric.DeviceType == x.Type)).OrderBy(x => x.Code).Take(8).ToListAsync(ct);
        foreach (var device in devices)
        {
            foreach (var metric in definitions.Where(x => x.DeviceType == device.Type))
            {
                if (!await db.SimulationStates.AsNoTracking().AnyAsync(x => x.Enabled, ct)) return;
                var latest = await db.TelemetrySamples.AsNoTracking().Where(x => x.DeviceId == device.Id && x.MetricCode == metric.Code).OrderByDescending(x => x.CollectedAt).Select(x => (decimal?)x.Value).FirstOrDefaultAsync(ct); var value = Math.Max(0m, (latest ?? 10m) + ((decimal)_random.NextDouble() - .5m) * 2m);
                await ingest.IngestAsync(new TelemetryIngestRequest(device.Id, metric.Code, value, metric.Unit, DateTimeOffset.UtcNow, $"sim-{device.Id:N}-{metric.Code}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"), "Simulation", ct);
            }
        }
    }

    private async Task SimulateVisitorCountsAsync(VisitorCountIngestService visitorCounts, CancellationToken ct)
    {
        var collectedAt = DateTimeOffset.UtcNow;
        var tick = collectedAt.ToUnixTimeSeconds();
        await visitorCounts.IngestSimulationBatchAsync(
            new VisitorCountRequest("In", _random.Next(1, 6), collectedAt, $"sim-visitor-in-{tick}"),
            new VisitorCountRequest("Out", _random.Next(1, 6), collectedAt, $"sim-visitor-out-{tick}"),
            ct);
    }
}
