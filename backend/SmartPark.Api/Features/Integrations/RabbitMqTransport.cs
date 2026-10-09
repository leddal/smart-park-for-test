using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SmartPark.Api.Features.Integrations;

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "park";
    public string Password { get; set; } = "ParkMessaging!2026";
    public string VirtualHost { get; set; } = "/";
    public string QueueName { get; set; } = "smartpark.integrations";
    public int ConsumerDelayMilliseconds { get; set; } = 1000;
}

public sealed class RabbitMqTransport(IOptions<RabbitMqOptions> options, ILogger<RabbitMqTransport> logger) : IOutboxPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private ConnectionFactory CreateFactory() => new()
    {
        HostName = options.Value.HostName, Port = options.Value.Port, UserName = options.Value.UserName,
        Password = options.Value.Password, VirtualHost = options.Value.VirtualHost,
        AutomaticRecoveryEnabled = false, RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
        ContinuationTimeout = TimeSpan.FromSeconds(5), ConsumerDispatchConcurrency = 1,
    };

    public async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        var queue = options.Value.QueueName;
        await channel.QueueDeclareAsync(queue + ".dead", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = queue + ".dead" }, cancellationToken: ct);
    }

    public async Task PublishAsync(IntegrationMessage message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var connection = await CreateFactory().CreateConnectionAsync(timeout.Token);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), timeout.Token);
        await DeclareAsync(channel, timeout.Token);
        var properties = new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = message.MessageId.ToString(), Type = message.Kind };
        // Mandatory + tracked confirms rejects unroutable/nacked publication, not merely a successful socket write.
        await channel.BasicPublishAsync("", options.Value.QueueName, mandatory: true, basicProperties: properties, body: JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions), cancellationToken: timeout.Token);
    }

    public async Task ConsumeAsync(Func<IntegrationMessage, CancellationToken, Task<DeliveryResult>> handle, CancellationToken ct)
    {
        await using var connection = await CreateFactory().CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await DeclareAsync(channel, ct);
        await channel.BasicQosAsync(0, 1, false, ct);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                IntegrationMessage? message;
                try { message = JsonSerializer.Deserialize<IntegrationMessage>(delivery.Body.Span, JsonOptions); }
                catch (JsonException)
                {
                    logger.LogWarning("Malformed integration message sent to dead-letter queue");
                    await channel.BasicNackAsync(delivery.DeliveryTag, false, false, ct);
                    return;
                }
                var result = message is null ? DeliveryResult.DeadLetter : await ProcessAsync(message);
                if (result == DeliveryResult.DeadLetter) await channel.BasicNackAsync(delivery.DeliveryTag, false, false, ct);
                else await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Integration consumer failed before acknowledgement");
                // A DB/ACK failure keeps the delivery replayable; closing the channel also requeues unacked messages.
                if (channel.IsOpen && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    await channel.BasicNackAsync(delivery.DeliveryTag, false, true, ct);
                }
            }
        };
        await channel.BasicConsumeAsync(options.Value.QueueName, autoAck: false, consumer: consumer, cancellationToken: ct);
        while (connection.IsOpen && channel.IsOpen) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        ct.ThrowIfCancellationRequested();
        throw new IOException("RabbitMQ consumer connection closed; reconnect required.");

        async Task<DeliveryResult> ProcessAsync(IntegrationMessage message)
        {
            // Deliberate local demonstration latency makes Published visibly distinct from the final receipt.
            if (options.Value.ConsumerDelayMilliseconds > 0) await Task.Delay(options.Value.ConsumerDelayMilliseconds, ct);
            return await handle(message, ct);
        }
    }
}
