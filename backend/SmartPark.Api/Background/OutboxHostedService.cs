using SmartPark.Api.Features.Integrations;

namespace SmartPark.Api.Background;

public sealed class OutboxHostedService(IServiceScopeFactory scopes, ILogger<OutboxHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Outbox dispatch failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private async Task DispatchAsync(CancellationToken ct)
    {
        for (var index = 0; index < 20 && !ct.IsCancellationRequested; index++)
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<OutboxDeliveryService>().PublishNextAsync(ct)) break;
        }
    }
}
