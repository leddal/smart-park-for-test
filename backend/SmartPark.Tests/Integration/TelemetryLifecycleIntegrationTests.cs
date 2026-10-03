using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartPark.Api.Background;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.IoT;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class TelemetryLifecycleIntegrationTests(SmartParkWebApplicationFactory factory)
{
    [Fact]
    public async Task Abnormal_repeat_recovery_old_sample_and_new_round_follow_alert_lifecycle_rules()
    {
        var subject = await CreateSubjectAsync();
        var now = DateTimeOffset.UtcNow;

        var firstAbnormal = await IngestAsync(subject.DeviceId, 10m, now.AddSeconds(-4), "first-abnormal");
        var repeatedAbnormal = await IngestAsync(subject.DeviceId, 9m, now.AddSeconds(-3), "repeat-abnormal");
        var recovery = await IngestAsync(subject.DeviceId, 30m, now.AddSeconds(-2), "recovery");
        var oldAbnormal = await IngestAsync(subject.DeviceId, 8m, now.AddSeconds(-5), "old-abnormal");
        var duplicate = await IngestAsync(subject.DeviceId, 8m, now.AddSeconds(-5), "old-abnormal");
        var newRound = await IngestAsync(subject.DeviceId, 8m, now.AddSeconds(-1), "new-round");

        Assert.NotNull(firstAbnormal.AlertId);
        Assert.NotNull(firstAbnormal.EventId);
        Assert.Null(repeatedAbnormal.AlertId);
        Assert.Null(recovery.AlertId);
        Assert.True(oldAbnormal.AppliedToLiveState is false);
        Assert.True(duplicate.AppliedToLiveState is false);
        Assert.NotNull(newRound.AlertId);
        Assert.NotEqual(firstAbnormal.AlertId, newRound.AlertId);

        var persisted = await factory.InDatabaseAsync(async db => new
        {
            Alerts = await db.Alerts.Where(x => x.RuleId == subject.RuleId).OrderBy(x => x.TriggeredAt).ToListAsync(),
            Events = await db.ParkEvents.Where(x => x.AssetId == subject.AssetId && x.Category == "Alert").ToListAsync(),
            Samples = await db.TelemetrySamples.Where(x => x.DeviceId == subject.DeviceId).ToListAsync(),
        });
        Assert.Equal(2, persisted.Alerts.Count);
        Assert.Single(persisted.Alerts, x => x.Status == "Recovered");
        Assert.Single(persisted.Alerts, x => x.Status == "Active");
        Assert.Equal(2, persisted.Events.Count);
        Assert.Equal(5, persisted.Samples.Count);
    }

    [Fact]
    public async Task Future_telemetry_is_rejected_without_persisting_a_sample()
    {
        var subject = await CreateSubjectAsync();
        using var manager = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(manager, HttpMethod.Post, "/api/iot/telemetry", new
        {
            deviceId = subject.DeviceId,
            metricCode = "soilMoisture",
            value = 10,
            unit = "%",
            collectedAt = DateTimeOffset.UtcNow.AddMinutes(6),
            idempotencyKey = $"future-{Guid.NewGuid():N}",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var sampleCount = await factory.InDatabaseAsync(db => db.TelemetrySamples.CountAsync(x => x.DeviceId == subject.DeviceId));
        Assert.Equal(0, sampleCount);
    }

    [Fact]
    public async Task Simulation_is_seeded_off_and_only_administrator_can_change_it()
    {
        using var visitor = await factory.CreateAuthenticatedClientAsync("visitor");
        using var forbidden = await SmartParkWebApplicationFactory.SendJsonAsync(visitor, HttpMethod.Post, "/api/iot/simulation", new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var enabled = await factory.InDatabaseAsync(db => db.SimulationStates.OrderBy(x => x.CreatedAt).Select(x => x.Enabled).FirstAsync());
        Assert.False(enabled);
    }

    [Fact]
    public async Task Simulation_disable_waits_for_the_shared_gate_and_returns_disabled()
    {
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        using var enableResponse = await SmartParkWebApplicationFactory.SendJsonAsync(admin, HttpMethod.Post, "/api/iot/simulation", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);

        var csrf = await SmartParkWebApplicationFactory.GetCsrfAsync(admin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/iot/simulation") { Content = JsonContent.Create(new { enabled = false }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var gate = factory.Services.GetRequiredService<SimulationGate>();
        Task<HttpResponseMessage> disable;
        using (var held = await gate.AcquireAsync(CancellationToken.None))
        {
            disable = admin.SendAsync(request);
            await Task.Delay(100);
            Assert.False(disable.IsCompleted);
        }

        using var response = await disable;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("enabled").GetBoolean());
        var enabled = await factory.InDatabaseAsync(db => db.SimulationStates.OrderBy(x => x.CreatedAt).Select(x => x.Enabled).FirstAsync());
        Assert.False(enabled);
    }

    [Fact]
    public async Task Simulation_visitor_count_is_labeled_idempotent_and_preserves_manual_samples()
    {
        var beforeInside = await factory.InDatabaseAsync(db => db.Parks.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => x.VisitorInsideCount).FirstAsync());
        var now = DateTimeOffset.UtcNow;
        var manualKey = $"manual-visitor-{Guid.NewGuid():N}";
        using var manager = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var manualResponse = await SmartParkWebApplicationFactory.SendJsonAsync(manager, HttpMethod.Post, "/api/iot/visitor-counts", new
        {
            direction = "In",
            count = 3,
            collectedAt = now,
            idempotencyKey = manualKey,
        });
        Assert.Equal(HttpStatusCode.Created, manualResponse.StatusCode);

        var inboundKey = $"simulation-visitor-in-{Guid.NewGuid():N}";
        var outboundKey = $"simulation-visitor-out-{Guid.NewGuid():N}";
        var collectedAt = now;
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<VisitorCountIngestService>();
        var firstSimulation = await service.IngestSimulationBatchAsync(
            new VisitorCountRequest("In", 2, collectedAt, inboundKey),
            new VisitorCountRequest("Out", 1, collectedAt, outboundKey));
        var repeatedSimulation = await service.IngestSimulationBatchAsync(
            new VisitorCountRequest("In", 2, collectedAt, inboundKey),
            new VisitorCountRequest("Out", 1, collectedAt, outboundKey));

        Assert.False(firstSimulation.Idempotent);
        Assert.True(repeatedSimulation.Idempotent);
        Assert.Equal(firstSimulation.Inbound.Id, repeatedSimulation.Inbound.Id);
        Assert.Equal(firstSimulation.Outbound.Id, repeatedSimulation.Outbound.Id);
        Assert.Equal(beforeInside + 4, firstSimulation.Inside);

        var persisted = await factory.InDatabaseAsync(async db => new
        {
            Inside = await db.Parks.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => x.VisitorInsideCount).FirstAsync(),
            Manual = await db.VisitorCounterSamples.AsNoTracking().SingleAsync(x => x.IdempotencyKey == manualKey),
            Simulations = await db.VisitorCounterSamples.AsNoTracking().Where(x => x.IdempotencyKey == inboundKey || x.IdempotencyKey == outboundKey).OrderBy(x => x.Direction).ToListAsync(),
        });
        Assert.Equal(beforeInside + 4, persisted.Inside);
        Assert.Equal("Manual", persisted.Manual.Source);
        Assert.Equal(3, persisted.Manual.Count);
        Assert.Equal(2, persisted.Simulations.Count);
        Assert.All(persisted.Simulations, sample => Assert.Equal("Simulation", sample.Source));
        Assert.Contains(persisted.Simulations, sample => sample.Direction == "In" && sample.Count == 2);
        Assert.Contains(persisted.Simulations, sample => sample.Direction == "Out" && sample.Count == 1);
    }

    [Fact]
    public async Task Simulation_batch_rejects_manual_idempotency_collisions_without_changing_visitor_counts()
    {
        var collectedAt = DateTimeOffset.UtcNow;
        var inboundKey = $"simulation-manual-in-{Guid.NewGuid():N}";
        var outboundKey = $"simulation-manual-out-{Guid.NewGuid():N}";
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<VisitorCountIngestService>();
        await service.IngestAsync(new VisitorCountRequest("In", 2, collectedAt, inboundKey), "Manual");
        await service.IngestAsync(new VisitorCountRequest("Out", 1, collectedAt, outboundKey), "Manual");
        var before = await factory.InDatabaseAsync(async db => new
        {
            Inside = await db.Parks.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => x.VisitorInsideCount).FirstAsync(),
            Samples = await db.VisitorCounterSamples.CountAsync(),
        });

        var conflict = await Assert.ThrowsAsync<ApiException>(() => service.IngestSimulationBatchAsync(
            new VisitorCountRequest("In", 2, collectedAt, inboundKey),
            new VisitorCountRequest("Out", 1, collectedAt, outboundKey)));
        Assert.Equal(409, conflict.StatusCode);

        var after = await factory.InDatabaseAsync(async db => new
        {
            Inside = await db.Parks.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => x.VisitorInsideCount).FirstAsync(),
            Samples = await db.VisitorCounterSamples.CountAsync(),
        });
        Assert.Equal(before.Inside, after.Inside);
        Assert.Equal(before.Samples, after.Samples);
    }

    [Fact]
    public async Task Outbound_visitor_count_cannot_make_inside_count_negative()
    {
        await factory.InDatabaseAsync(async db =>
        {
            var park = await db.Parks.OrderBy(x => x.CreatedAt).FirstAsync();
            park.VisitorInsideCount = 0;
            await db.SaveChangesAsync();
        });
        var samplesBefore = await factory.InDatabaseAsync(db => db.VisitorCounterSamples.CountAsync());
        using var manager = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(manager, HttpMethod.Post, "/api/iot/visitor-counts", new
        {
            direction = "Out",
            count = 1,
            collectedAt = DateTimeOffset.UtcNow,
            idempotencyKey = $"negative-visitor-{Guid.NewGuid():N}",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.InDatabaseAsync(async db => new
        {
            Inside = await db.Parks.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => x.VisitorInsideCount).FirstAsync(),
            Samples = await db.VisitorCounterSamples.CountAsync(),
        });
        Assert.Equal(0, persisted.Inside);
        Assert.Equal(samplesBefore, persisted.Samples);
    }

    [Fact]
    public async Task Latest_and_overview_queries_return_one_metric_per_device_with_deterministic_ties()
    {
        var subject = await CreateSubjectAsync();
        var now = DateTimeOffset.UtcNow;
        var idPrefix = Guid.NewGuid().ToString("N")[..30];
        var lowerId = Guid.ParseExact($"{idPrefix}01", "N");
        var higherId = Guid.ParseExact($"{idPrefix}02", "N");
        await factory.InDatabaseAsync(async db =>
        {
            db.TelemetrySamples.AddRange(
                new TelemetrySample { DeviceId = subject.DeviceId, MetricCode = "soilMoisture", Value = 1, Unit = "%", CollectedAt = now.AddMinutes(-1), Source = "Manual" },
                new TelemetrySample { Id = lowerId, DeviceId = subject.DeviceId, MetricCode = "soilMoisture", Value = 25, Unit = "%", CollectedAt = now, Source = "Manual" },
                new TelemetrySample { Id = higherId, DeviceId = subject.DeviceId, MetricCode = "soilMoisture", Value = 35, Unit = "%", CollectedAt = now, Source = "Manual" });
            await db.SaveChangesAsync();
        });
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SmartPark.Api.Features.Overview.OverviewCacheService>().InvalidateAsync();
        using var manager = await factory.CreateAuthenticatedClientAsync("dispatcher");
        foreach (var path in new[] { "/api/iot/latest", "/api/overview/summary" })
        {
            using var response = await manager.GetAsync(path);
            response.EnsureSuccessStatusCode();
            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var metrics = path.EndsWith("summary", StringComparison.Ordinal) ? document.RootElement.GetProperty("metrics") : document.RootElement;
            var metric = Assert.Single(metrics.EnumerateArray(), item => item.GetProperty("deviceId").GetGuid() == subject.DeviceId);
            Assert.Equal(35m, metric.GetProperty("value").GetDecimal());
            Assert.Equal("soilMoisture", metric.GetProperty("metricCode").GetString());
            Assert.False(metric.GetProperty("stale").GetBoolean());
        }
    }

    private async Task<TelemetrySubject> CreateSubjectAsync()
    {
        return await factory.InDatabaseAsync(async db =>
        {
            var asset = new Asset
            {
                Code = $"TEL-ASSET-{Guid.NewGuid():N}",
                PublicCode = $"P-TEL-{Guid.NewGuid():N}",
                Name = "Telemetry lifecycle test asset",
                Category = "Sensor",
                Status = "Active",
            };
            var device = new Device
            {
                Asset = asset,
                Code = $"TEL-DEVICE-{Guid.NewGuid():N}",
                Type = "Soil",
                Enabled = true,
            };
            var rule = new AlertRule
            {
                DeviceId = device.Id,
                MetricCode = "soilMoisture",
                Unit = "%",
                Lower = 20m,
                RecoveryLower = 25m,
                Severity = "Warning",
                Enabled = true,
            };
            db.Devices.Add(device);
            db.AlertRules.Add(rule);
            await db.SaveChangesAsync();
            return new TelemetrySubject(asset.Id, device.Id, rule.Id);
        });
    }

    private async Task<TelemetryIngestResult> IngestAsync(Guid deviceId, decimal value, DateTimeOffset collectedAt, string idempotencyKey)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TelemetryIngestService>();
        return await service.IngestAsync(new TelemetryIngestRequest(deviceId, "soilMoisture", value, "%", collectedAt, idempotencyKey), "Manual");
    }

    private readonly record struct TelemetrySubject(Guid AssetId, Guid DeviceId, Guid RuleId);
}
