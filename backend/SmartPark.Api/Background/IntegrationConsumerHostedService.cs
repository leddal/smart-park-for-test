using SmartPark.Api.Features.Integrations;

namespace SmartPark.Api.Background;

public sealed class IntegrationConsumerHostedService(IServiceScopeFactory scopes, RabbitMqTransport transport, ILogger<IntegrationConsumerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await transport.ConsumeAsync(async (message, ct) =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    return await scope.ServiceProvider.GetRequiredService<OutboxDeliveryService>().ConsumeAsync(message, ct);
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "RabbitMQ consumer unavailable; reconnecting"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
