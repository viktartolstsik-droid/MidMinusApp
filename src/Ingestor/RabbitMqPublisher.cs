using System.Text.Json;
using Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Ingestor;

public sealed class RabbitMqPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqPublisher> logger)
    : IReadingPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(IReadOnlyList<RawReadingMessage> messages, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            try
            {
                foreach (var message in messages)
                {
                    var properties = new BasicProperties
                    {
                        Persistent = true,
                        ContentType = RawReadingTransport.ContentType,
                        MessageId = message.MessageId.ToString(),
                        Type = nameof(RawReadingMessage),
                        Timestamp = new AmqpTimestamp(message.IngestedAt.ToUnixTimeSeconds()),
                    };
                    var body = JsonSerializer.SerializeToUtf8Bytes(message, RawReadingTransport.JsonOptions);

                    // With confirmation tracking this returns only after the broker has accepted the message.
                    await channel.BasicPublishAsync(
                        exchange: string.Empty,
                        routingKey: _options.Queue,
                        mandatory: true,
                        basicProperties: properties,
                        body: body,
                        cancellationToken: cancellationToken);
                }
            }
            catch
            {
                await ResetAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            return channel.IsOpen;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }

    // Automatic recovery is off: a broken channel is dropped here and the next call reconnects and redeclares the queue.
    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await ResetAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);

        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            ClientProvidedName = "ingestor",
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = _options.ConnectTimeout,
        };

        try
        {
            _connection = await factory.CreateConnectionAsync(timeout.Token);
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                timeout.Token);
            await _channel.QueueDeclareAsync(
                queue: RawReadingTransport.DeadLetterQueueFor(_options.Queue),
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: timeout.Token);
            await _channel.QueueDeclareAsync(
                queue: _options.Queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: RawReadingTransport.QueueArguments(_options.Queue),
                cancellationToken: timeout.Token);
        }
        catch
        {
            await ResetAsync();
            throw;
        }

        logger.LogInformation(
            "Connected to RabbitMQ at {Host}:{Port}, queue {Queue}, dead-letter queue {DeadLetterQueue}",
            _options.Host, _options.Port, _options.Queue, RawReadingTransport.DeadLetterQueueFor(_options.Queue));
        return _channel;
    }

    private async Task ResetAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;

        try
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Closing a broken RabbitMQ connection failed");
        }
    }
}
