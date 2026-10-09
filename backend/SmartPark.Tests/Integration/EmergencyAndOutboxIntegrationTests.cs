using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPark.Api.Background;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Emergency;
using SmartPark.Api.Features.Integrations;

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
        var messageId = await CreatePendingMessageAsync(forceFailure: true);

        // Publication only hands the message to RabbitMQ now, so the consumer must run as well for the
        // simulated delivery failure to advance the business attempt counter.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var (publisher, consumer) = await StartWorkersAsync(cancellation.Token);
        try
        {
            for (var expectedAttempts = 1; expectedAttempts <= 5; expectedAttempts++)
            {
                var state = await WaitForOutboxAsync(messageId, message => message.Attempts >= expectedAttempts, TimeSpan.FromSeconds(30), cancellation.Token);
                if (expectedAttempts >= 5) continue;
                // A failed consumer receipt schedules the next business retry into the future; only this
                // message's due time is accelerated so the isolated database does not wait out the real backoff.
                Assert.True(state.NextAttemptAt > state.ConsumedAt);
                await ExpediteAsync(messageId);
            }
        }
        finally
        {
            await StopWorkersAsync(publisher, consumer);
        }

        var result = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId),
            Attempts = await db.IntegrationAttempts.AsNoTracking().Where(x => x.OutboxMessageId == messageId).ToListAsync(),
        });
        Assert.Equal("Failed", result.Message.Status);
        Assert.Equal(5, result.Message.Attempts);
        var consumeLogs = result.Attempts.Where(x => x.Stage == "Consume").ToList();
        Assert.Equal(5, consumeLogs.Count);
        Assert.All(consumeLogs, attempt => Assert.False(attempt.Success));
        // The broker confirmed every publication; only the simulated consumer receipt kept failing.
        var publishLogs = result.Attempts.Where(x => x.Stage == "Publish").ToList();
        Assert.Equal(5, publishLogs.Count);
        Assert.All(publishLogs, attempt => Assert.True(attempt.Success));
        Assert.Equal(5, result.Message.PublishAttempts);
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

    [Fact]
    public async Task Published_broker_confirmation_is_not_a_consumer_receipt()
    {
        var messageId = await CreatePendingMessageAsync(makeOldestPending: true);
        // A recording publisher confirms publication without the real broker so no consumer can turn it
        // into a receipt; the database side of the flow stays real.
        var recorder = new RecordingPublisher();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = new OutboxDeliveryService(scope.ServiceProvider.GetRequiredService<ParkDbContext>(), recorder, NullLogger<OutboxDeliveryService>.Instance);
            Assert.True(await service.PublishNextAsync(CancellationToken.None));
        }

        var published = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId),
            ConsumeLogs = await db.IntegrationAttempts.AsNoTracking().CountAsync(x => x.OutboxMessageId == messageId && x.Stage == "Consume"),
        });
        Assert.Equal("Published", published.Message.Status);
        Assert.NotEqual("Succeeded", published.Message.Status);
        Assert.Equal(1, published.Message.PublishAttempts);
        Assert.Equal(0, published.Message.Attempts);
        Assert.NotNull(published.Message.PublishedAt);
        Assert.Null(published.Message.ConsumedAt);
        Assert.Equal(0, published.ConsumeLogs);
        var delivery = Assert.Single(recorder.Deliveries);
        Assert.Equal(messageId, delivery.MessageId);
        Assert.Equal(1, delivery.Attempt);
        Assert.Equal(0, delivery.Generation);
    }

    [Fact]
    public async Task Successful_message_flows_through_rabbitmq_in_two_stages_with_a_single_receipt()
    {
        var messageId = await CreatePendingMessageAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var (publisher, consumer) = await StartWorkersAsync(cancellation.Token);
        try
        {
            await WaitForOutboxAsync(messageId, message => message.Status == "Succeeded", TimeSpan.FromSeconds(45), cancellation.Token);
        }
        finally
        {
            await StopWorkersAsync(publisher, consumer);
        }

        var delivered = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId),
            Logs = await db.IntegrationAttempts.AsNoTracking().Where(x => x.OutboxMessageId == messageId).ToListAsync(),
        });
        Assert.Equal("Succeeded", delivered.Message.Status);
        Assert.Equal(1, delivered.Message.Attempts);
        Assert.Equal(1, delivered.Message.PublishAttempts);
        Assert.NotNull(delivered.Message.PublishedAt);
        Assert.NotNull(delivered.Message.ConsumedAt);
        Assert.True(delivered.Message.ConsumedAt >= delivered.Message.PublishedAt);
        Assert.True(delivered.Message.PublishedAt >= delivered.Message.CreatedAt);
        Assert.Contains(delivered.Logs, x => x.Stage == "Publish" && x.Success);
        Assert.Contains(delivered.Logs, x => x.Stage == "Consume" && x.Success);

        // A duplicate broker delivery of the same durable envelope must reuse the committed receipt.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = new OutboxDeliveryService(scope.ServiceProvider.GetRequiredService<ParkDbContext>(), new RecordingPublisher(), NullLogger<OutboxDeliveryService>.Instance);
            var duplicate = new IntegrationMessage(messageId, "Assets", 1, delivered.Message.CreatedAt, 0, 1);
            Assert.Equal(DeliveryResult.Acknowledge, await service.ConsumeAsync(duplicate, CancellationToken.None));
        }

        var afterDuplicate = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId),
            Logs = await db.IntegrationAttempts.AsNoTracking().Where(x => x.OutboxMessageId == messageId).ToListAsync(),
        });
        Assert.Equal("Succeeded", afterDuplicate.Message.Status);
        Assert.Equal(1, afterDuplicate.Message.Attempts);
        Assert.Single(afterDuplicate.Logs, x => x.Stage == "Consume" && x.Attempt == 1);
        Assert.Single(afterDuplicate.Logs, x => x.Stage == "Publish" && x.Attempt == 1);

        // The API keeps the attempt counter and the immutable receipt log independent.
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        using var response = await admin.GetAsync("/api/integrations/messages?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == messageId);
        Assert.Equal(1, item.GetProperty("attempts").GetInt32());
        var attemptLogs = item.GetProperty("attemptLogs").EnumerateArray().ToList();
        Assert.Equal(2, attemptLogs.Count);
        Assert.Contains(attemptLogs, x => x.GetProperty("stage").GetString() == "Publish");
        Assert.Contains(attemptLogs, x => x.GetProperty("stage").GetString() == "Consume");
    }

    [Fact]
    public async Task Manual_retry_only_replays_failed_messages_and_preserves_generation_zero_receipts()
    {
        // A failed generation 0 message with its original publish/consume receipts.
        var messageId = await SeedFailedMessageAsync();
        var notFailedId = await CreatePendingMessageAsync();
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");

        // Only a failed message may be replayed.
        using (var rejected = await SmartParkWebApplicationFactory.SendJsonAsync(admin, HttpMethod.Post, $"/api/integrations/messages/{notFailedId}/retry", null))
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using (var accepted = await SmartParkWebApplicationFactory.SendJsonAsync(admin, HttpMethod.Post, $"/api/integrations/messages/{messageId}/retry", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
            Assert.Equal(1, body.RootElement.GetProperty("generation").GetInt32());
            Assert.Equal("Pending", body.RootElement.GetProperty("status").GetString());
        }

        var replayed = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId),
            Logs = await db.IntegrationAttempts.AsNoTracking().Where(x => x.OutboxMessageId == messageId).ToListAsync(),
        });
        Assert.Equal(1, replayed.Message.Generation);
        Assert.Equal("Pending", replayed.Message.Status);
        Assert.Equal(0, replayed.Message.PublishAttempts);
        Assert.Equal(0, replayed.Message.PublishFailures);
        Assert.Equal(0, replayed.Message.Attempts);
        Assert.Null(replayed.Message.PublishedAt);
        Assert.Null(replayed.Message.ConsumedAt);
        Assert.Null(replayed.Message.LastError);
        // Generation 0 evidence is retained instead of being erased or renumbered.
        var generationZero = replayed.Logs.Where(x => x.Generation == 0).ToList();
        Assert.Equal(5, generationZero.Count(x => x.Stage == "Publish"));
        Assert.Equal(5, generationZero.Count(x => x.Stage == "Consume"));

        // A stale delivery from the previous generation is acknowledged without touching the new one.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = new OutboxDeliveryService(scope.ServiceProvider.GetRequiredService<ParkDbContext>(), new RecordingPublisher(), NullLogger<OutboxDeliveryService>.Instance);
            Assert.Equal(DeliveryResult.Acknowledge, await service.ConsumeAsync(new IntegrationMessage(messageId, "Assets", 1, replayed.Message.CreatedAt, 0, 1), CancellationToken.None));
        }
        var afterStale = await factory.InDatabaseAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId));
        Assert.Equal("Pending", afterStale.Status);
        Assert.Equal(0, afterStale.Attempts);

        // Generation 1 flows through the real broker and succeeds.
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
        {
            var (publisher, consumer) = await StartWorkersAsync(cancellation.Token);
            try
            {
                await WaitForOutboxAsync(messageId, message => message.Status == "Succeeded", TimeSpan.FromSeconds(45), cancellation.Token);
            }
            finally
            {
                await StopWorkersAsync(publisher, consumer);
            }
        }

        var final = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId),
            Logs = await db.IntegrationAttempts.AsNoTracking().Where(x => x.OutboxMessageId == messageId).ToListAsync(),
        });
        Assert.Equal("Succeeded", final.Message.Status);
        Assert.Equal(1, final.Message.Generation);
        Assert.Equal(1, final.Message.Attempts);
        Assert.Contains(final.Logs, x => x.Generation == 1 && x.Stage == "Publish" && x.Success);
        Assert.Contains(final.Logs, x => x.Generation == 1 && x.Stage == "Consume" && x.Success);
        Assert.Equal(generationZero.Count, final.Logs.Count(x => x.Generation == 0));

        // A message that already succeeded is not replayable.
        using (var forbidden = await SmartParkWebApplicationFactory.SendJsonAsync(admin, HttpMethod.Post, $"/api/integrations/messages/{messageId}/retry", null))
            Assert.Equal(HttpStatusCode.Conflict, forbidden.StatusCode);
    }

    [Fact]
    public async Task Publisher_exception_stays_pending_then_fails_without_recording_a_consumption()
    {
        var messageId = await CreatePendingMessageAsync(makeOldestPending: true);
        var message = await factory.InDatabaseAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId));
        await using var scope = factory.Services.CreateAsyncScope();
        var service = new OutboxDeliveryService(scope.ServiceProvider.GetRequiredService<ParkDbContext>(), new ThrowingPublisher(), NullLogger<OutboxDeliveryService>.Instance);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.True(await service.PublishNextAsync(CancellationToken.None));
            var state = await factory.InDatabaseAsync(db => db.OutboxMessages.AsNoTracking().Where(x => x.Id == messageId).Select(x => new { x.Status, x.PublishFailures }).SingleAsync());
            Assert.Equal(attempt, state.PublishFailures);
            Assert.Equal(attempt < 5 ? "Pending" : "Failed", state.Status);
            // Only this message's due time is pushed into the past; other fixture rows stay untouched.
            if (attempt < 5) await ExpediteAsync(messageId);
        }

        var result = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId),
            PublishLogs = await db.IntegrationAttempts.AsNoTracking().Where(x => x.OutboxMessageId == messageId && x.Stage == "Publish").ToListAsync(),
            ConsumeLogs = await db.IntegrationAttempts.AsNoTracking().CountAsync(x => x.OutboxMessageId == messageId && x.Stage == "Consume"),
        });
        Assert.Equal("Failed", result.Message.Status);
        // The broker-outage budget is separate from the simulated consumer attempts.
        Assert.Equal(5, result.Message.PublishAttempts);
        Assert.Equal(5, result.Message.PublishFailures);
        Assert.Equal(0, result.Message.Attempts);
        Assert.Null(result.Message.PublishedAt);
        Assert.Null(result.Message.ConsumedAt);
        Assert.NotNull(result.Message.LastError);
        Assert.Equal(5, result.PublishLogs.Count);
        Assert.All(result.PublishLogs, log => Assert.False(log.Success));
        Assert.Equal(0, result.ConsumeLogs);
        // The durable business record survives the publication outage.
        Assert.Equal("Assets", result.Message.Kind);
        Assert.Equal(message.PayloadJson, result.Message.PayloadJson);
    }

    [Fact]
    public async Task Malformed_or_mismatched_deliveries_are_dead_lettered_without_touching_business_data()
    {
        var messageId = await CreatePendingMessageAsync();
        var before = await factory.InDatabaseAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId));
        await using var scope = factory.Services.CreateAsyncScope();
        var service = new OutboxDeliveryService(scope.ServiceProvider.GetRequiredService<ParkDbContext>(), new RecordingPublisher(), NullLogger<OutboxDeliveryService>.Instance);
        var invalid = new[]
        {
            new IntegrationMessage(Guid.Empty, "Assets", 1, before.CreatedAt, 0, 1),
            new IntegrationMessage(messageId, "Assets", 2, before.CreatedAt, 0, 1),
            new IntegrationMessage(messageId, "Assets", 1, before.CreatedAt, 0, 0),
            new IntegrationMessage(messageId, "Assets", 1, before.CreatedAt, 0, 6),
            new IntegrationMessage(messageId, "Assets", 1, before.CreatedAt, -1, 1),
            new IntegrationMessage(messageId, " ", 1, before.CreatedAt, 0, 1),
            new IntegrationMessage(messageId, "VisitorCount", 1, before.CreatedAt, 0, 1),
            new IntegrationMessage(Guid.NewGuid(), "Assets", 1, before.CreatedAt, 0, 1),
        };

        foreach (var delivery in invalid) Assert.Equal(DeliveryResult.DeadLetter, await service.ConsumeAsync(delivery, CancellationToken.None));

        var after = await factory.InDatabaseAsync(async db => new
        {
            Message = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId),
            Attempts = await db.IntegrationAttempts.AsNoTracking().CountAsync(x => x.OutboxMessageId == messageId),
        });
        Assert.Equal(before.Status, after.Message.Status);
        Assert.Equal(before.Attempts, after.Message.Attempts);
        Assert.Equal(before.PublishAttempts, after.Message.PublishAttempts);
        Assert.Equal(before.PublishedAt, after.Message.PublishedAt);
        Assert.Equal(before.ConsumedAt, after.Message.ConsumedAt);
        Assert.Equal(0, after.Attempts);
    }

    [Fact]
    public async Task Invalid_json_is_rejected_to_a_real_broker_dead_letter_queue()
    {
        TestEnvironment.RequireIsolatedMessaging();
        var configured = factory.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        // This test owns a unique pair of broker queues; it never purges the shared verification queue.
        var options = new RabbitMqOptions { HostName = configured.HostName, Port = configured.Port, UserName = configured.UserName, Password = configured.Password, VirtualHost = configured.VirtualHost, QueueName = configured.QueueName + "." + Guid.NewGuid().ToString("N"), ConsumerDelayMilliseconds = 0 };
        var transport = new RabbitMqTransport(Options.Create(options), NullLogger<RabbitMqTransport>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        var connectionFactory = new ConnectionFactory { HostName = options.HostName, Port = options.Port, UserName = options.UserName, Password = options.Password, VirtualHost = options.VirtualHost };
        await using var connection = await connectionFactory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), ct);
        await transport.DeclareAsync(channel, ct);
        var consume = transport.ConsumeAsync((_, _) => throw new InvalidOperationException("Invalid JSON must not reach the business handler."), ct);
        try
        {
            var id = Guid.NewGuid().ToString();
            await channel.BasicPublishAsync("", options.QueueName, true, new BasicProperties { Persistent = true, MessageId = id }, Encoding.UTF8.GetBytes("not-json"), ct);
            BasicGetResult? deadLetter = null;
            while (deadLetter is null)
            {
                deadLetter = await channel.BasicGetAsync(options.QueueName + ".dead", autoAck: false, ct);
                if (deadLetter is null) await Task.Delay(100, ct);
            }
            Assert.Equal(id, deadLetter.BasicProperties.MessageId);
            Assert.Equal("not-json", Encoding.UTF8.GetString(deadLetter.Body.Span));
            await channel.BasicAckAsync(deadLetter.DeliveryTag, false, ct);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await consume; } catch (OperationCanceledException) { }
            // Delete only queues created by this test, after the consumer has released them.
            await channel.QueueDeleteAsync(options.QueueName, ifUnused: false, ifEmpty: false, cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(options.QueueName + ".dead", ifUnused: false, ifEmpty: false, cancellationToken: CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pipeline_counts_are_database_workflow_totals_and_remain_manager_only()
    {
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        using var response = await admin.GetAsync("/api/integrations/pipeline");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("RabbitMQ", body.RootElement.GetProperty("transport").GetString());
        Assert.EndsWith(".dead", body.RootElement.GetProperty("deadLetterQueueName").GetString());
        var counts = await factory.InDatabaseAsync(db => db.OutboxMessages.AsNoTracking().GroupBy(x => x.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Status, x => x.Count));
        Assert.Equal(counts.GetValueOrDefault("Pending"), body.RootElement.GetProperty("pending").GetInt32());
        Assert.Equal(counts.GetValueOrDefault("Published"), body.RootElement.GetProperty("published").GetInt32());
        using var worker = await factory.CreateAuthenticatedClientAsync("worker");
        using var denied = await worker.GetAsync("/api/integrations/pipeline");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
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

    // The hosted workers are removed from the test host, so the real publish/consume loop is started
    // explicitly against the shared PostgreSQL fixture and the real RabbitMQ broker.
    private async Task<(OutboxHostedService Publisher, IntegrationConsumerHostedService Consumer)> StartWorkersAsync(CancellationToken ct)
    {
        TestEnvironment.RequireIsolatedMessaging();
        var scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var publisher = new OutboxHostedService(scopes, NullLogger<OutboxHostedService>.Instance);
        var consumer = new IntegrationConsumerHostedService(scopes, factory.Services.GetRequiredService<RabbitMqTransport>(), NullLogger<IntegrationConsumerHostedService>.Instance);
        await publisher.StartAsync(ct);
        await consumer.StartAsync(ct);
        return (publisher, consumer);
    }

    private static async Task StopWorkersAsync(OutboxHostedService publisher, IntegrationConsumerHostedService consumer)
    {
        await consumer.StopAsync(CancellationToken.None);
        await publisher.StopAsync(CancellationToken.None);
        consumer.Dispose();
        publisher.Dispose();
    }

    private async Task<OutboxMessage> WaitForOutboxAsync(Guid messageId, Func<OutboxMessage, bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var message = await factory.InDatabaseAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId, ct));
            if (condition(message)) return message;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException($"Outbox message {messageId} did not reach the expected state within {timeout}.");
    }

    // PublishNextAsync claims the oldest eligible Pending row, so a test that must target one specific
    // message backdates only that row. ParkDbContext.Stamp rewrites CreatedAt for Added rows, not Modified ones.
    private async Task<Guid> CreatePendingMessageAsync(bool forceFailure = false, bool makeOldestPending = false)
    {
        return await factory.InDatabaseAsync(async db =>
        {
            var platform = new IntegrationPlatform { Name = $"Outbox regression {Guid.NewGuid():N}", Enabled = true, ForceFailure = forceFailure };
            var message = new OutboxMessage { PlatformId = platform.Id, Kind = "Assets", PayloadJson = "{\"test\":true}", Status = "Pending", NextAttemptAt = DateTimeOffset.UtcNow };
            db.AddRange(platform, message);
            await db.SaveChangesAsync();
            if (makeOldestPending)
            {
                message.CreatedAt = DateTimeOffset.UnixEpoch;
                await db.SaveChangesAsync();
            }
            return message.Id;
        });
    }

    // Reproduces the persisted outcome of the forced-failure flow so replay semantics can be verified
    // without driving another five real broker round trips.
    private async Task<Guid> SeedFailedMessageAsync()
    {
        return await factory.InDatabaseAsync(async db =>
        {
            var platform = new IntegrationPlatform { Name = $"Retry test {Guid.NewGuid():N}", Enabled = true, ForceFailure = false };
            var message = new OutboxMessage
            {
                PlatformId = platform.Id, Kind = "Assets", PayloadJson = "{\"test\":true}", Status = "Failed",
                Generation = 0, PublishAttempts = 5, PublishFailures = 0, Attempts = 5,
                PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-2), ConsumedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                LastError = "Local simulation forced to fail; no data was sent to an external platform.",
            };
            db.AddRange(platform, message);
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                db.IntegrationAttempts.Add(new IntegrationAttempt { OutboxMessageId = message.Id, Generation = 0, Stage = "Publish", Attempt = attempt, Success = true, Message = "RabbitMQ confirmed publication; awaiting local simulation consumer. No external platform was contacted." });
                db.IntegrationAttempts.Add(new IntegrationAttempt { OutboxMessageId = message.Id, Generation = 0, Stage = "Consume", Attempt = attempt, Success = false, Message = "Local simulation forced to fail; no data was sent to an external platform." });
            }
            await db.SaveChangesAsync();
            return message.Id;
        });
    }

    // Only this message's due time is moved into the past; other fixture messages keep their own schedule.
    private Task ExpediteAsync(Guid messageId) => factory.InDatabaseAsync(async db =>
    {
        var message = await db.OutboxMessages.SingleAsync(x => x.Id == messageId);
        message.NextAttemptAt = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        await db.SaveChangesAsync();
    });

    private sealed class RecordingPublisher : IOutboxPublisher
    {
        public List<IntegrationMessage> Deliveries { get; } = [];
        public Task PublishAsync(IntegrationMessage message, CancellationToken ct) { Deliveries.Add(message); return Task.CompletedTask; }
    }

    private sealed class ThrowingPublisher : IOutboxPublisher
    {
        public Task PublishAsync(IntegrationMessage message, CancellationToken ct) => throw new InvalidOperationException("Simulated broker outage; no publication was confirmed.");
    }

    private readonly record struct EventState(Guid Id, int Version, string Status = "");
}
