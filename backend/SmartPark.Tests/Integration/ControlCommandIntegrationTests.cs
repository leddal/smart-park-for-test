using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPark.Api.Background;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class ControlCommandIntegrationTests(SmartParkWebApplicationFactory factory)
{
    [Fact]
    public async Task Control_rejects_noncontrollable_device_invalid_brightness_and_invalid_duration()
    {
        var ids = await DeviceIdsAsync();
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");

        using var wrongType = await SendCommandAsync(dispatcher, ids.Sensor, "Start", durationSeconds: 1);
        using var badBrightness = await SendCommandAsync(dispatcher, ids.Lamp, "Brightness", brightness: 101);
        using var badDuration = await SendCommandAsync(dispatcher, ids.Irrigation, "Start", durationSeconds: 0);

        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badBrightness.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badDuration.StatusCode);
    }

    [Fact]
    public async Task Durable_command_queue_records_failure_timeout_and_ignores_an_old_control_receipt()
    {
        var ids = await DeviceIdsAsync();
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        var failed = await QueueCommandAsync(dispatcher, ids.Irrigation, "Start", durationSeconds: 30, outcome: "Fail");
        var timedOut = await QueueCommandAsync(dispatcher, ids.Lamp, "Brightness", brightness: 50, outcome: "Timeout");
        var oldStart = await QueueCommandAsync(dispatcher, ids.Irrigation, "Start", durationSeconds: 30);
        var latestStop = await QueueCommandAsync(dispatcher, ids.Irrigation, "Stop");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var service = new CommandHostedService(factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CommandHostedService>.Instance);
        await service.StartAsync(cancellation.Token);
        try
        {
            await WaitForProcessedAsync([failed.Id, timedOut.Id, oldStart.Id, latestStop.Id], cancellation.Token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var records = await factory.InDatabaseAsync(async db => new
        {
            Failed = await db.ControlCommands.SingleAsync(x => x.Id == failed.Id),
            TimedOut = await db.ControlCommands.SingleAsync(x => x.Id == timedOut.Id),
            OldStart = await db.ControlCommands.SingleAsync(x => x.Id == oldStart.Id),
            LatestStop = await db.ControlCommands.SingleAsync(x => x.Id == latestStop.Id),
            DeviceState = await db.Devices.Where(x => x.Id == ids.Irrigation).Select(x => x.ControlState).SingleAsync(),
        });
        Assert.Equal("Failed", records.Failed.Status);
        Assert.Equal("TimedOut", records.TimedOut.Status);
        Assert.Equal("SimulatedSucceeded", records.OldStart.Status);
        Assert.Contains("ignored", records.OldStart.Receipt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SimulatedSucceeded", records.LatestStop.Status);
        Assert.Equal("Stopped", records.DeviceState);
    }

    private async Task<(Guid Irrigation, Guid Lamp, Guid Sensor)> DeviceIdsAsync() => await factory.InDatabaseAsync(async db =>
    (
        await db.Devices.Where(x => x.Type == "Irrigation").Select(x => x.Id).FirstAsync(),
        await db.Devices.Where(x => x.Type == "Lamp").Select(x => x.Id).FirstAsync(),
        await db.Devices.Where(x => x.Type == "Soil").Select(x => x.Id).FirstAsync()));

    private static Task<HttpResponseMessage> SendCommandAsync(HttpClient client, Guid deviceId, string action, int? durationSeconds = null, int? brightness = null, string? outcome = null) =>
        SmartParkWebApplicationFactory.SendJsonAsync(client, HttpMethod.Post, "/api/control/commands", new { deviceId, action, durationSeconds, brightness, outcome });

    private static async Task<CommandState> QueueCommandAsync(HttpClient client, Guid deviceId, string action, int? durationSeconds = null, int? brightness = null, string? outcome = null)
    {
        using var response = await SendCommandAsync(client, deviceId, action, durationSeconds, brightness, outcome);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new CommandState(payload.RootElement.GetProperty("id").GetGuid(), payload.RootElement.GetProperty("sequence").GetInt64());
    }

    private async Task WaitForProcessedAsync(IEnumerable<Guid> commandIds, CancellationToken ct)
    {
        var ids = commandIds.ToArray();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            var pending = await factory.InDatabaseAsync(db => db.ControlCommands.CountAsync(x => ids.Contains(x.Id) && x.Status == "Pending"));
            if (pending == 0) return;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("The command service did not process queued control commands.");
    }

    private readonly record struct CommandState(Guid Id, long Sequence);
}
