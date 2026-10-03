using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPark.Api.Background;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Emergency;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class EmergencyAndOutboxIntegrationTests(SmartParkWebApplicationFactory factory)
{
    [Fact]
    public async Task Event_cannot_close_with_unfinished_work_and_emits_outbox_only_after_valid_closure()
    {
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        var eventState = await CreateEventAsync(dispatcher);
        await factory.InDatabaseAsync(async db =>
        {
            db.WorkOrders.Add(new WorkOrder
            {
                Number = $"WO-EVENT-{Guid.NewGuid():N}",
                Title = "Unfinished event work",
                Type = "Inspection",
                EventId = eventState.Id,
                Status = "Assigned",
            });
            await db.SaveChangesAsync();
        });

        var pending = await TransitionAsync(dispatcher, eventState.Id, "RequestClosure", eventState.Version, "Request closure after review");
        using var blocked = await SmartParkWebApplicationFactory.SendJsonAsync(dispatcher, HttpMethod.Post, $"/api/emergency/events/{eventState.Id}/transition", new { action = "Close", version = pending.Version, text = "Attempt closure" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        await factory.InDatabaseAsync(async db =>
        {
            var work = await db.WorkOrders.SingleAsync(x => x.EventId == eventState.Id);
            work.Status = "Completed";
            await db.SaveChangesAsync();
        });
        var closed = await TransitionAsync(dispatcher, eventState.Id, "Close", pending.Version, "All work completed and verified");

        Assert.Equal("Closed", closed.Status);
        var outbox = await factory.InDatabaseAsync(db => db.OutboxMessages.Where(x => x.Kind == "EventClosed" && x.PayloadJson.Contains(eventState.Id.ToString())).SingleAsync());
        Assert.Equal("Pending", outbox.Status);
    }

    [Fact]
    public async Task Event_cannot_close_while_related_alert_is_active()
    {
        var eventId = await factory.InDatabaseAsync(async db =>
        {
            var device = await db.Devices.FirstAsync(x => x.Type == "Soil");
            var rule = new AlertRule { DeviceId = device.Id, MetricCode = "soilMoisture", Unit = "%", Lower = 20m, Severity = "Warning", Enabled = true };
            db.AlertRules.Add(rule);
            var alert = new Alert { DeviceId = device.Id, RuleId = rule.Id, MetricCode = rule.MetricCode, Value = 10m, Severity = rule.Severity, Status = "Active", TriggeredAt = DateTimeOffset.UtcNow };
            var item = new ParkEvent { Number = $"EVT-ACTIVE-{Guid.NewGuid():N}", Title = "Active alert closure guard", Category = "Alert", Severity = "Warning", AlertId = alert.Id, Status = "PendingClosure" };
            alert.EventId = item.Id;
            db.AddRange(alert, item);
            await db.SaveChangesAsync();
            return item.Id;
        });
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var close = await SmartParkWebApplicationFactory.SendJsonAsync(dispatcher, HttpMethod.Post, $"/api/emergency/events/{eventId}/transition", new { action = "Close", version = 0, text = "Alert is still active" });

        Assert.Equal(HttpStatusCode.Conflict, close.StatusCode);
    }

    [Fact]
    public async Task Event_creation_persists_business_record_and_outbox_in_one_transaction()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EmergencyService>();
        var before = await factory.InDatabaseAsync(async db => new { Events = await db.ParkEvents.CountAsync(), Outbox = await db.OutboxMessages.CountAsync() });
        var created = await service.CreateAsync(new EventRequest($"Atomic event {Guid.NewGuid():N}", "Inspection", "Warning", null, null, "Atomic outbox test", null, null), Guid.NewGuid());
        var after = await factory.InDatabaseAsync(async db => new
        {
            Event = await db.ParkEvents.SingleAsync(x => x.Id == created.Id),
            Messages = await db.OutboxMessages.Where(x => x.Kind == "EventCreated" && x.PayloadJson.Contains(created.Id.ToString())).ToListAsync(),
            Events = await db.ParkEvents.CountAsync(),
            Outbox = await db.OutboxMessages.CountAsync(),
        });

        Assert.Equal("Open", after.Event.Status);
        Assert.Single(after.Messages);
        Assert.Equal(before.Events + 1, after.Events);
        Assert.Equal(before.Outbox + 1, after.Outbox);
    }

    [Fact]
    public async Task Forced_outbox_failure_retries_five_times_then_stops_until_manual_retry()
    {
        var messageId = await factory.InDatabaseAsync(async db =>
        {
            var platform = new IntegrationPlatform { Name = $"Failure test {Guid.NewGuid():N}", Enabled = true, ForceFailure = true };
            var message = new OutboxMessage { PlatformId = platform.Id, Kind = "Assets", PayloadJson = "{\"test\":true}", Status = "Pending", NextAttemptAt = DateTimeOffset.UtcNow };
            db.AddRange(platform, message);
            await db.SaveChangesAsync();
            return message.Id;
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var service = new OutboxHostedService(factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxHostedService>.Instance);
        await service.StartAsync(cancellation.Token);
        try
        {
            for (var expectedAttempts = 1; expectedAttempts <= 5; expectedAttempts++)
            {
                await WaitForAttemptsAsync(messageId, expectedAttempts, cancellation.Token);
                if (expectedAttempts < 5)
                {
                    await factory.InDatabaseAsync(async db =>
                    {
                        var message = await db.OutboxMessages.SingleAsync(x => x.Id == messageId);
                        message.NextAttemptAt = DateTimeOffset.UtcNow.AddMilliseconds(-1);
                        await db.SaveChangesAsync();
                    });
                }
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var result = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.SingleAsync(x => x.Id == messageId),
            Attempts = await db.IntegrationAttempts.Where(x => x.OutboxMessageId == messageId).OrderBy(x => x.Attempt).ToListAsync(),
        });
        Assert.Equal("Failed", result.Message.Status);
        Assert.Equal(5, result.Message.Attempts);
        Assert.Equal(5, result.Attempts.Count);
        Assert.All(result.Attempts, attempt => Assert.False(attempt.Success));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Messages_expose_attempt_count_and_receipt_logs_even_when_platform_is_missing(bool missingPlatform)
    {
        var receipt = $"Local simulated receipt {Guid.NewGuid():N}";
        var messageId = await factory.InDatabaseAsync(async db =>
        {
            var platform = new IntegrationPlatform { Name = $"Message test {Guid.NewGuid():N}" };
            var message = new OutboxMessage { PlatformId = platform.Id, Kind = "Assets", Status = "SimulatedSucceeded", Attempts = 1 };
            if (!missingPlatform) db.IntegrationPlatforms.Add(platform);
            db.AddRange(message, new IntegrationAttempt { OutboxMessageId = message.Id, Attempt = 1, Success = true, Message = receipt });
            await db.SaveChangesAsync();
            return message.Id;
        });
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        using var response = await admin.GetAsync("/api/integrations/messages?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == messageId);
        Assert.Equal(1, message.GetProperty("attempts").GetInt32());
        Assert.Equal(receipt, message.GetProperty("detail").GetString());
        var attempt = Assert.Single(message.GetProperty("attemptLogs").EnumerateArray());
        Assert.True(attempt.GetProperty("success").GetBoolean());
        Assert.Equal(receipt, attempt.GetProperty("message").GetString());
        if (missingPlatform) Assert.Equal("平台已不存在", message.GetProperty("platformName").GetString());
        else Assert.StartsWith("Message test ", message.GetProperty("platformName").GetString());
    }

    private async Task<EventState> CreateEventAsync(HttpClient dispatcher)
    {
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(dispatcher, HttpMethod.Post, "/api/emergency/events", new { title = $"Closure test {Guid.NewGuid():N}", category = "Inspection", severity = "Warning", description = "Integration test event" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new EventState(document.RootElement.GetProperty("id").GetGuid(), document.RootElement.GetProperty("version").GetInt32());
    }

    private static async Task<EventState> TransitionAsync(HttpClient client, Guid id, string action, int version, string text)
    {
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(client, HttpMethod.Post, $"/api/emergency/events/{id}/transition", new { action, version, text });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new EventState(id, document.RootElement.GetProperty("version").GetInt32(), document.RootElement.GetProperty("status").GetString()!);
    }

    private async Task WaitForAttemptsAsync(Guid messageId, int expectedAttempts, CancellationToken ct)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(12))
        {
            var attempts = await factory.InDatabaseAsync(db => db.OutboxMessages.Where(x => x.Id == messageId).Select(x => x.Attempts).SingleAsync(ct));
            if (attempts >= expectedAttempts) return;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException($"Outbox message {messageId} did not reach attempt {expectedAttempts}.");
    }

    private readonly record struct EventState(Guid Id, int Version, string Status = "");
}
