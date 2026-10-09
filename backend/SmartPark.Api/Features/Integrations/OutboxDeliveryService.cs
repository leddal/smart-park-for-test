using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Data;

namespace SmartPark.Api.Features.Integrations;

// The existing outbox ID is the event ID. Generation separates explicit replays from old broker deliveries.
public sealed record IntegrationMessage(Guid MessageId, string Kind, int Version, DateTimeOffset OccurredAt, int Generation, int Attempt);
public interface IOutboxPublisher { Task PublishAsync(IntegrationMessage message, CancellationToken ct); }
public enum DeliveryResult { Acknowledge, DeadLetter }

public sealed class OutboxDeliveryService(ParkDbContext db, IOutboxPublisher publisher, ILogger<OutboxDeliveryService> logger)
{
    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(new[] { 1, 5, 15, 60 }[Math.Clamp(attempt - 1, 0, 3)]);

    public async Task<bool> PublishNextAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        // Holding the row until confirmation/commit also makes an early consumer wait for Published.
        var item = (await db.OutboxMessages.FromSqlInterpolated($"""
            SELECT * FROM "OutboxMessages"
            WHERE "Status" = 'Pending' AND "NextAttemptAt" <= {now}
            ORDER BY "CreatedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct)).SingleOrDefault();
        if (item is null) return false;
        item.PublishAttempts++;
        var success = false;
        var text = "RabbitMQ confirmed publication; awaiting local simulation consumer. No external platform was contacted.";
        try
        {
            await publisher.PublishAsync(new IntegrationMessage(item.Id, item.Kind, 1, item.CreatedAt, item.Generation, item.Attempts + 1), ct);
            success = true;
            item.Status = "Published";
            item.PublishFailures = 0;
            item.PublishedAt = DateTimeOffset.UtcNow;
            item.LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RabbitMQ publication failed for {MessageId} generation {Generation}", item.Id, item.Generation);
            text = "RabbitMQ publication was not confirmed; message remains in PostgreSQL for retry. No external platform was contacted.";
            item.LastError = text;
            // Successful publications do not spend the broker-outage retry budget.
            item.PublishFailures++;
            item.Status = item.PublishFailures >= 5 ? "Failed" : "Pending";
            item.NextAttemptAt = DateTimeOffset.UtcNow + RetryDelay(item.PublishFailures);
        }
        db.IntegrationAttempts.Add(new IntegrationAttempt { OutboxMessageId = item.Id, Generation = item.Generation, Stage = "Publish", Attempt = item.PublishAttempts, Success = success, Message = text });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // A crash after broker confirmation but before this commit may publish twice. ConsumeAsync deduplicates it.
        db.ChangeTracker.Clear();
        return true;
    }

    public async Task<DeliveryResult> ConsumeAsync(IntegrationMessage delivery, CancellationToken ct)
    {
        if (delivery.MessageId == Guid.Empty || delivery.Version != 1 || delivery.Generation < 0 || delivery.Attempt is < 1 or > 5 || string.IsNullOrWhiteSpace(delivery.Kind))
            return DeliveryResult.DeadLetter;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = (await db.OutboxMessages.FromSqlInterpolated($"SELECT * FROM \"OutboxMessages\" WHERE \"Id\" = {delivery.MessageId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (item is null) return DeliveryResult.DeadLetter;
        if (delivery.Generation != item.Generation) return DeliveryResult.Acknowledge;
        if (delivery.Kind != item.Kind) return DeliveryResult.DeadLetter;
        if (item.Status == "Failed") return DeliveryResult.DeadLetter;
        if (delivery.Attempt <= item.Attempts || item.Status is "Succeeded" or "SimulatedSucceeded") return DeliveryResult.Acknowledge;
        // Pending can occur when a publisher confirmed but its DB commit failed. The durable delivery is still valid.
        if (delivery.Attempt != item.Attempts + 1 || item.Status is not ("Pending" or "Published")) return DeliveryResult.DeadLetter;
        var platform = item.PlatformId is null ? null : await db.IntegrationPlatforms.SingleOrDefaultAsync(x => x.Id == item.PlatformId, ct);
        var failure = item.PlatformId is not null && (platform is null || !platform.Enabled || platform.ForceFailure);
        var text = failure ? "Local simulation forced to fail; no data was sent to an external platform." : "Local simulation succeeded; data was not sent to an external platform.";
        item.Attempts++;
        item.ConsumedAt = DateTimeOffset.UtcNow;
        item.PublishedAt ??= item.ConsumedAt;
        item.LastError = failure ? text : null;
        item.Status = !failure ? "Succeeded" : item.Attempts >= 5 ? "Failed" : "Pending";
        if (failure) item.NextAttemptAt = DateTimeOffset.UtcNow + RetryDelay(item.Attempts);
        db.IntegrationAttempts.Add(new IntegrationAttempt { OutboxMessageId = item.Id, Generation = item.Generation, Stage = "Consume", Attempt = item.Attempts, Success = !failure, Message = text });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // Receipt + deduplication progress share one DB transaction; only the caller then ACKs RabbitMQ.
        return item.Status == "Failed" ? DeliveryResult.DeadLetter : DeliveryResult.Acknowledge;
    }
}
