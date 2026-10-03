using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Data;

namespace SmartPark.Api.Background;

public sealed class OutboxHostedService(IServiceScopeFactory scopes, ILogger<OutboxHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Outbox dispatch failed"); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
    private async Task DispatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>(); var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages.Where(x => x.Status == "Pending" && x.NextAttemptAt <= now).OrderBy(x => x.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var message in messages)
        {
            var platform = message.PlatformId is null ? null : await db.IntegrationPlatforms.SingleOrDefaultAsync(x => x.Id == message.PlatformId, ct);
            message.Attempts++;
            var failure = platform is not null && (!platform.Enabled || platform.ForceFailure);
            var text = failure ? "Local simulation forced to fail; no data was sent to an external platform." : "Local simulation succeeded; data was not sent to an external platform.";
            db.IntegrationAttempts.Add(new IntegrationAttempt { OutboxMessageId = message.Id, Attempt = message.Attempts, Success = !failure, Message = text });
            if (!failure) { message.Status = "Succeeded"; message.LastError = null; }
            else if (message.Attempts >= 5) { message.Status = "Failed"; message.LastError = text; }
            else { message.LastError = text; message.NextAttemptAt = now.AddSeconds(new[] { 1, 5, 15, 60 }[message.Attempts - 1]); }
        }
        if (messages.Count > 0) await db.SaveChangesAsync(ct);
    }
}
