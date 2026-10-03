using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;

namespace SmartPark.Api.Data;

public sealed class DemoSeeder(ParkDbContext db, UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, IConfiguration configuration)
{
    private const string CurrentSeed = "initial-v2";
    private const string LegacySeed = "initial-v1";
    private const string GisSupplementSeed = "seed-gis-v2";
    private const string DomBounds = "{\"southWest\":{\"lng\":121.435,\"lat\":31.164},\"northEast\":{\"lng\":121.440,\"lat\":31.168}}";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (bool.TryParse(configuration["Demo:Enabled"], out var demoEnabled) && !demoEnabled) return;

        var hasCurrentSeed = await db.SeedVersions.AnyAsync(x => x.Name == CurrentSeed, ct);
        var hasLegacySeed = await db.SeedVersions.AnyAsync(x => x.Name == LegacySeed, ct);
        if (hasCurrentSeed) return;
        if (hasLegacySeed)
        {
            await SupplementLegacySeedAsync(ct);
            return;
        }

        foreach (var role in new[] { ParkRoles.Administrator, ParkRoles.Dispatcher, ParkRoles.Worker, ParkRoles.Visitor })
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole<Guid>(role));
        var password = configuration["Demo:Password"] ?? "ParkDemo!2026";
        await EnsureUserAsync("admin", "系统管理员", ParkRoles.Administrator, password);
        await EnsureUserAsync("dispatcher", "值班调度员", ParkRoles.Dispatcher, password);
        await EnsureUserAsync("worker", "养护作业员", ParkRoles.Worker, password);
        await EnsureUserAsync("worker2", "巡检作业员", ParkRoles.Worker, password);
        await EnsureUserAsync("visitor", "演示游客", ParkRoles.Visitor, password);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var park = new Park
        {
            Name = configuration["Demo:ParkName"] ?? "澄园智慧公园",
            Description = "本地演示公园",
            OpenHours = "06:00-22:00",
            Phone = "021-55550000",
            Longitude = 121.437500m,
            Latitude = 31.166000m
        };
        var north = new ParkZone { Park = park, Name = "南门服务区", Code = "SOUTH_GATE", Description = "入口与游客服务设施" };
        var lake = new ParkZone { Park = park, Name = "湖畔生态区", Code = "LAKESIDE", Description = "水体与植物养护区" };
        db.AddRange(park, north, lake);
        db.Tags.AddRange(new[] { new Tag { Name = "亲子", Color = "#409eff" }, new Tag { Name = "无障碍", Color = "#67c23a" }, new Tag { Name = "生态观察", Color = "#e6a23c" } });
        var activity = new Activity { Title = "周末生态导览", Description = "本地示例活动，预约免费。", Location = "南门服务台", Status = "Published" };
        activity.Sessions.Add(new ActivitySession { StartsAt = DateTimeOffset.UtcNow.AddDays(2), EndsAt = DateTimeOffset.UtcNow.AddDays(2).AddHours(2), Capacity = 20, Status = "Published" });
        db.Activities.Add(activity);
        db.Announcements.Add(new Announcement { Title = "欢迎来到智慧公园", Body = "公告为公开演示内容。", Status = "Published", StartsAt = DateTimeOffset.UtcNow.AddDays(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(30), PublishedAt = DateTimeOffset.UtcNow });
        await AddSeedMapLayersAsync(ct);
        await AddSeedDomAsync(ct);

        var definitions = new[]
        {
            ("soilMoisture", "土壤湿度", "%", "Soil"), ("waterLevel", "水位", "m", "WaterLevel"), ("ph", "pH", "pH", "WaterQuality"), ("dissolvedOxygen", "溶解氧", "mg/L", "WaterQuality"), ("turbidity", "浊度", "NTU", "WaterQuality"),
            ("noise", "噪声", "dB", "Noise"), ("temperature", "温度", "°C", "Weather"), ("humidity", "湿度", "%", "Weather"), ("pm25", "PM2.5", "μg/m³", "Weather"), ("windSpeed", "风速", "m/s", "Weather"), ("rainfall", "雨量", "mm", "Weather"), ("occupancy", "占用率", "%", "Toilet"), ("pestCount", "虫情数量", "count", "Pest")
        };
        db.MetricDefinitions.AddRange(definitions.Select(x => new MetricDefinition { Code = x.Item1, Name = x.Item2, Unit = x.Item3, DeviceType = x.Item4 }));
        var typeMetrics = new Dictionary<string, (string Code, string Unit, decimal Value)[]>
        {
            ["Soil"] = [("soilMoisture", "%", 42m)], ["WaterLevel"] = [("waterLevel", "m", 1.5m)], ["WaterQuality"] = [("ph", "pH", 7.2m), ("dissolvedOxygen", "mg/L", 6.6m), ("turbidity", "NTU", 4m)], ["Toilet"] = [("occupancy", "%", 25m)], ["Noise"] = [("noise", "dB", 48m)],
            ["Weather"] = [("temperature", "°C", 22m), ("humidity", "%", 58m), ("pm25", "μg/m³", 29m), ("windSpeed", "m/s", 2.1m), ("rainfall", "mm", 0m)], ["Pest"] = [("pestCount", "count", 3m)]
        };
        var devices = new List<Device>();
        var deviceSeeds = new[]
        {
            ("Soil", "土壤墒情传感器", 121.436000m, 31.165000m), ("WaterLevel", "湖区水位计", 121.437500m, 31.165000m), ("WaterQuality", "湖区水质监测仪", 121.438500m, 31.166500m), ("Toilet", "南门智慧公厕", 121.436000m, 31.167000m),
            ("Noise", "园路噪声监测仪", 121.436000m, 31.165000m), ("Weather", "微型气象站", 121.437500m, 31.165000m), ("Pest", "虫情测报灯", 121.438500m, 31.166500m), ("Camera", "南门视频资源", 121.436000m, 31.167000m),
            ("Irrigation", "生态区灌溉阀", 121.436000m, 31.165000m), ("Lamp", "湖畔智慧路灯", 121.437500m, 31.165000m), ("Broadcast", "园区数字广播", 121.438500m, 31.166500m)
        };
        foreach (var seed in deviceSeeds)
        {
            var category = seed.Item1 is "Soil" or "WaterLevel" or "WaterQuality" or "Noise" or "Weather" or "Pest" ? "Sensor" : "Facility";
            var asset = new Asset { Code = $"AST-{seed.Item1.ToUpperInvariant()}", Name = seed.Item2, Category = category, Zone = seed.Item1 is "WaterLevel" or "WaterQuality" ? lake : north, Longitude = seed.Item3, Latitude = seed.Item4, PublicCode = $"P-{seed.Item1.ToUpperInvariant()}", PublicDescription = $"{seed.Item2}公开演示介绍", Status = "Active" };
            if (category == "Facility") asset.Facility = new FacilityProfile { Kind = seed.Item1, Specification = "演示台账", Quantity = 1 };
            var device = new Device { Asset = asset, Code = $"DEV-{seed.Item1.ToUpperInvariant()}", Type = seed.Item1, Enabled = true, Model = "Demo-1", Manufacturer = "SmartPark", ControlState = "Stopped" };
            devices.Add(device);
            db.Devices.Add(device);
        }
        var plant = new Asset { Code = "AST-PLANT-001", Name = "香樟示例树", Category = "Plant", Zone = lake, Longitude = 121.436000m, Latitude = 31.167000m, Status = "Active", PublicCode = "P-PLANT-001", PublicDescription = "香樟公开介绍", Plant = new PlantProfile { Species = "香樟", DiameterCm = 35m, HeightM = 8m, Health = "Good" } };
        db.Assets.Add(plant);
        await db.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var device in devices)
        {
            if (!typeMetrics.TryGetValue(device.Type, out var metrics)) continue;
            foreach (var metric in metrics)
                for (var hour = 24; hour >= 0; hour--) db.TelemetrySamples.Add(new TelemetrySample { DeviceId = device.Id, MetricCode = metric.Code, Unit = metric.Unit, Value = metric.Value + (hour % 4), CollectedAt = now.AddHours(-hour), Source = "Seed", IdempotencyKey = $"seed-{device.Id:N}-{metric.Code}-{hour}" });
            device.LastTelemetryAt = now;
        }
        AddVisitorHistory(park, now);
        var worker = await users.FindByNameAsync("worker") ?? throw new InvalidOperationException("Seed worker missing.");
        var sampleEvent = new ParkEvent { Number = "EVT-SEED-001", Title = "示例巡检事件", Category = "Inspection", Severity = "Warning", AssetId = plant.Id, Description = "用于演示处置闭环的种子事件", Status = "Open", Longitude = plant.Longitude, Latitude = plant.Latitude };
        var work = new WorkOrder { Number = "WO-SEED-001", Title = "示例植物巡检", Type = "Inspection", AssetId = plant.Id, Event = sampleEvent, AssigneeId = worker.Id, DueAt = now.AddDays(1), Priority = "Normal", Status = "Assigned", DetailsJson = "{\"area\":\"湖畔生态区\"}" };
        work.Checklist.Add(new WorkOrderChecklistItem { Name = "检查树体健康" });
        work.Logs.Add(new WorkOrderLog { UserId = worker.Id, Action = "Created", Text = "种子示例工单" });
        db.AddRange(sampleEvent, work);
        db.EventLogs.Add(new EventLog { Event = sampleEvent, Action = "Created", Text = "种子示例事件" });
        db.AlertRules.Add(new AlertRule { DeviceId = devices.First(x => x.Type == "Soil").Id, MetricCode = "soilMoisture", Unit = "%", Lower = 20m, RecoveryLower = 25m, Severity = "Warning", Enabled = true });
        db.SimulationStates.Add(new SimulationState { Enabled = false });
        db.MediaMaterials.Add(new MediaMaterial { Name = "默认广播音频", SamplePath = "samples/broadcast.wav", ContentType = "audio/wav" });
        foreach (var name in new[] { "上海市智慧公园综合管理平台", "区城运汇治理系统", "徐汇区视觉中枢平台", "徐汇区绿化动态管理信息系统", "随申码运行管理平台" }) db.IntegrationPlatforms.Add(new IntegrationPlatform { Name = name, Enabled = true });
        db.OutboxMessages.Add(new OutboxMessage { Kind = "EventCreated", PayloadJson = "{\"event\":\"EVT-SEED-001\"}", Status = "Pending" });
        db.SeedVersions.Add(new SeedVersion { Name = CurrentSeed });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task SupplementLegacySeedAsync(CancellationToken ct)
    {
        if (await db.SeedVersions.AnyAsync(x => x.Name == GisSupplementSeed, ct)) return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AddSeedMapLayersAsync(ct);
        await AddSeedDomAsync(ct);
        db.SeedVersions.Add(new SeedVersion { Name = GisSupplementSeed });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task AddSeedMapLayersAsync(CancellationToken ct)
    {
        foreach (var (name, kind) in new[] { ("示例公园边界", "Boundary"), ("示例园路", "Road"), ("示例兴趣点", "POI") })
            if (!await db.MapLayers.AnyAsync(x => x.Name == name, ct))
                db.MapLayers.Add(new MapLayer { Name = name, Kind = kind, IsPublic = true, GeoJson = LoadLayerGeoJson(kind) });
    }

    private async Task AddSeedDomAsync(CancellationToken ct)
    {
        const string layerName = "示例园区影像";
        if (await db.MapLayers.AnyAsync(x => x.Name == layerName, ct)) return;
        var sample = FindSample("park-dom.png");
        if (sample is null) return;
        var root = Path.GetFullPath(configuration["Storage:Root"] ?? "/uploads");
        Directory.CreateDirectory(root);
        const string storageName = "seed-park-dom.png";
        var target = Path.Combine(root, storageName);
        if (!File.Exists(target)) File.Copy(sample, target, overwrite: false);
        var stored = await db.StoredFiles.SingleOrDefaultAsync(x => x.StorageName == storageName, ct);
        if (stored is null)
        {
            stored = new StoredFile { StorageName = storageName, OriginalName = "park-dom.png", ContentType = "image/png", Length = new FileInfo(target).Length, Kind = "dom", IsPublic = true, IsTemporary = false };
            db.StoredFiles.Add(stored);
        }
        db.MapLayers.Add(new MapLayer { Name = layerName, Kind = "DOM", IsPublic = true, FileId = stored.Id, BoundsJson = DomBounds });
    }

    private void AddVisitorHistory(Park park, DateTimeOffset now)
    {
        var inside = 0;
        for (var hour = 24; hour > 0; hour--)
        {
            var entered = 8 + hour % 5;
            var exited = 4 + hour % 3;
            db.VisitorCounterSamples.Add(new VisitorCounterSample { Direction = "In", Count = entered, Unit = "People", CollectedAt = now.AddHours(-hour), Source = "Seed", IdempotencyKey = $"seed-visitor-in-{hour}" });
            db.VisitorCounterSamples.Add(new VisitorCounterSample { Direction = "Out", Count = exited, Unit = "People", CollectedAt = now.AddHours(-hour).AddMinutes(30), Source = "Seed", IdempotencyKey = $"seed-visitor-out-{hour}" });
            inside += entered - exited;
        }
        park.VisitorInsideCount = inside;
    }

    private static string LoadLayerGeoJson(string kind)
    {
        var sample = FindSample("park.geojson");
        if (sample is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sample));
                var features = doc.RootElement.GetProperty("features").EnumerateArray()
                    .Where(feature => feature.TryGetProperty("properties", out var properties) && ((properties.TryGetProperty("layer", out var layer) && layer.GetString() == kind) || (properties.TryGetProperty("kind", out var featureKind) && featureKind.GetString() == kind)))
                    .Select(feature => feature.GetRawText());
                return $"{{\"type\":\"FeatureCollection\",\"features\":[{string.Join(',', features)}]}}";
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return kind switch
        {
            "Boundary" => "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"name\":\"示例边界\"},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[121.435,31.164],[121.440,31.164],[121.440,31.168],[121.435,31.168],[121.435,31.164]]]}}]}",
            "POI" => "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"name\":\"南门\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[121.436,31.165]}}]}",
            _ => "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"name\":\"示例园路\"},\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[121.436,31.165],[121.4375,31.165],[121.4385,31.1665],[121.436,31.167]]}}]}"
        };
    }

    private static string? FindSample(string name)
    {
        var candidates = new[]
        {
            Path.Combine("/app/samples", name), Path.Combine("/workspace/samples", name), Path.Combine(AppContext.BaseDirectory, "samples", name),
            Path.Combine(Directory.GetCurrentDirectory(), "samples", name), Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "samples", name))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task EnsureUserAsync(string userName, string displayName, string role, string password)
    {
        var user = await users.FindByNameAsync(userName);
        if (user is null)
        {
            user = new AppUser { Id = Guid.NewGuid(), UserName = userName, DisplayName = displayName, EmailConfirmed = true, LockoutEnabled = true };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
        }
        if (!await users.IsInRoleAsync(user, role))
        {
            var result = await users.AddToRoleAsync(user, role);
            if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
        }
    }
}
