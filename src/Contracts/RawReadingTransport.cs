using System.Text.Json;

namespace Contracts;

public static class RawReadingTransport
{
    public const string QueueName = "ingestor.raw";

    public const string ContentType = "application/json";

    // Camel case matches the field names of RawReadingMessage in SDD §4.1.
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Returns to the queue a message survives before RabbitMQ dead-letters it instead of delivering it again.
    public const int DeliveryLimit = 5;

    public static string DeadLetterQueueFor(string queue) => $"{queue}.dlq";

    // RabbitMQ refuses to redeclare a queue with a different type or arguments, so every service declares it with exactly these.
    // A message rejected without requeue, or requeued past the delivery limit, goes through the default exchange
    // to the dead-letter queue (SDD §4.4). The quorum queue keeps the delivery count, so it survives consumer restarts.
    public static Dictionary<string, object?> QueueArguments(string queue) => new()
    {
        ["x-queue-type"] = "quorum",
        ["x-delivery-limit"] = DeliveryLimit,
        ["x-dead-letter-exchange"] = string.Empty,
        ["x-dead-letter-routing-key"] = DeadLetterQueueFor(queue),
    };
}
