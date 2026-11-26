using System.Text.Json;
using Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Testcontainers.RabbitMq;

namespace Ingestor.Tests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string UserName = "test";
    public const string Password = "test";

    public RabbitMqContainer Container { get; } = new RabbitMqBuilder("rabbitmq:3-management-alpine")
        .WithUsername(UserName)
        .WithPassword(Password)
        .Build();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class RabbitMqPublisherTests(RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    private static readonly DateTimeOffset IngestedAt = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Publish_PutsPersistentJsonMessagesIntoDurableQueue()
    {
        var queue = $"test.{Guid.NewGuid():N}";
        var message = new RawReadingMessage(
            MessageId: Guid.NewGuid(),
            IngestedAt: IngestedAt,
            SourceId: "Kitchen:air_quality.co2",
            Location: "Kitchen",
            Type: "air_quality.co2",
            Value: 640m,
            Unit: null,
            RawPayload: JsonDocument.Parse("""{ "type": "air_quality", "name": "Kitchen" }""").RootElement);

        await using (var publisher = CreatePublisher(rabbit.Container.Hostname, rabbit.Container.GetMappedPublicPort(5672), queue))
        {
            await publisher.PublishAsync([message], CancellationToken.None);
        }

        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var delivery = await channel.BasicGetAsync(queue, autoAck: true);

        Assert.NotNull(delivery);
        Assert.True(delivery.BasicProperties.Persistent);
        Assert.Equal("application/json", delivery.BasicProperties.ContentType);
        Assert.Equal(message.MessageId.ToString(), delivery.BasicProperties.MessageId);

        using var body = JsonDocument.Parse(delivery.Body);
        var root = body.RootElement;
        Assert.Equal("Kitchen", root.GetProperty("location").GetString());
        Assert.Equal(640m, root.GetProperty("value").GetDecimal());
        Assert.Equal("Kitchen:air_quality.co2", root.GetProperty("sourceId").GetString());
        Assert.Equal(IngestedAt, root.GetProperty("ingestedAt").GetDateTimeOffset());
        Assert.Equal("Kitchen", root.GetProperty("rawPayload").GetProperty("name").GetString());

        var roundTrip = JsonSerializer.Deserialize<RawReadingMessage>(delivery.Body.Span, RawReadingTransport.JsonOptions);
        Assert.Equal(message.SourceId, roundTrip?.SourceId);

        var deadLetterQueue = RawReadingTransport.DeadLetterQueueFor(queue);

        // The Processor declares the queue from the same contract; RabbitMQ accepts that only if the arguments match exactly.
        await DeclareAsync(connection, queue, durable: true, RawReadingTransport.QueueArguments(queue));
        await DeclareAsync(connection, deadLetterQueue, durable: true, arguments: null);

        // A quorum queue is always durable; a classic redeclare is refused only if the queue really is quorum.
        var classic = RawReadingTransport.QueueArguments(queue);
        classic.Remove("x-queue-type");
        classic.Remove("x-delivery-limit");
        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            DeclareAsync(connection, queue, durable: true, classic));

        // Only durability differs here, so the refusal proves the dead-letter queue is durable.
        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            DeclareAsync(connection, deadLetterQueue, durable: false, arguments: null));
    }

    [Fact]
    public async Task RequeuedPastDeliveryLimit_GoesToDeadLetterQueue()
    {
        var queue = $"test.{Guid.NewGuid():N}";
        var message = Message("Kitchen");
        await using (var publisher = CreatePublisher(rabbit.Container.Hostname, rabbit.Container.GetMappedPublicPort(5672), queue))
        {
            await publisher.PublishAsync([message], CancellationToken.None);
        }

        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        var deliveries = 0;
        while (deliveries < 20 && await channel.BasicGetAsync(queue, autoAck: false) is { } delivery)
        {
            deliveries++;
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
        }

        Assert.Equal(RawReadingTransport.DeliveryLimit + 1, deliveries);
        var deadLetter = await WaitForDeadLetterAsync(channel, queue);
        Assert.Equal(message.MessageId.ToString(), deadLetter.BasicProperties.MessageId);
    }

    [Fact]
    public async Task RejectedMessage_GoesToDeadLetterQueue()
    {
        var queue = $"test.{Guid.NewGuid():N}";
        var message = Message("Kitchen");
        await using (var publisher = CreatePublisher(rabbit.Container.Hostname, rabbit.Container.GetMappedPublicPort(5672), queue))
        {
            await publisher.PublishAsync([message], CancellationToken.None);
        }

        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var delivery = await channel.BasicGetAsync(queue, autoAck: false);
        Assert.NotNull(delivery);
        await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);

        var deadLetter = await WaitForDeadLetterAsync(channel, queue);
        Assert.Equal(message.MessageId.ToString(), deadLetter.BasicProperties.MessageId);
        Assert.Equal(0u, await channel.MessageCountAsync(queue));
    }

    [Fact]
    public async Task Publish_ReconnectsAfterBrokerClosedTheConnection()
    {
        var queue = $"test.{Guid.NewGuid():N}";
        await using var publisher = CreatePublisher(rabbit.Container.Hostname, rabbit.Container.GetMappedPublicPort(5672), queue);
        await publisher.PublishAsync([Message("Kitchen")], CancellationToken.None);

        var closed = await rabbit.Container.ExecAsync(["rabbitmqctl", "close_all_connections", "test"]);
        Assert.Equal(0, closed.ExitCode);

        // Like the poll loop: a publish on the dropped channel may fail once, the following one reconnects.
        var published = false;
        for (var attempt = 0; attempt < 3 && !published; attempt++)
        {
            try
            {
                await publisher.PublishAsync([Message("Garage")], CancellationToken.None);
                published = true;
            }
            catch (Exception) when (attempt < 2)
            {
            }
        }

        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        Assert.Equal(2u, await channel.MessageCountAsync(queue));
    }

    [Fact]
    public async Task UnreachableBroker_PublishThrowsAndReadyIsUnhealthy()
    {
        await using var publisher = CreatePublisher("127.0.0.1", 1, "unused");

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync([Message("Kitchen")], CancellationToken.None));

        var health = await new RabbitMqHealthCheck(publisher).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
    }

    private static RabbitMqPublisher CreatePublisher(string host, int port, string queue) =>
        new(
            Options.Create(new RabbitMqOptions
            {
                Host = host,
                Port = port,
                UserName = RabbitMqFixture.UserName,
                Password = RabbitMqFixture.Password,
                Queue = queue,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            }),
            NullLogger<RabbitMqPublisher>.Instance);

    private Task<IConnection> ConnectAsync() =>
        new ConnectionFactory { Uri = new Uri(rabbit.Container.GetConnectionString()) }.CreateConnectionAsync();

    // Dead-lettering is asynchronous, so the message may reach the dead-letter queue a moment later.
    private static async Task<BasicGetResult> WaitForDeadLetterAsync(IChannel channel, string queue)
    {
        var deadLetterQueue = RawReadingTransport.DeadLetterQueueFor(queue);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await channel.BasicGetAsync(deadLetterQueue, autoAck: true) is { } deadLetter)
            {
                return deadLetter;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No message reached {deadLetterQueue}.");
    }

    // A refused declare closes the channel, so each declare gets its own.
    private static async Task DeclareAsync(IConnection connection, string queue, bool durable, IDictionary<string, object?>? arguments)
    {
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable, exclusive: false, autoDelete: false, arguments);
    }

    private static RawReadingMessage Message(string location) =>
        new(Guid.NewGuid(), IngestedAt, $"{location}:energy", location, "energy", 1.5m, null, null);
}
