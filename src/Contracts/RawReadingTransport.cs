using System.Text.Json;

namespace Contracts;

public static class RawReadingTransport
{
    public const string QueueName = "ingestor.raw";

    public const string ContentType = "application/json";

    // Camel case matches the field names of RawReadingMessage in SDD §4.1.
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string DeadLetterQueueFor(string queue) => $"{queue}.dlq";

    // RabbitMQ refuses to redeclare a queue with different arguments, so every service declares it with exactly these.
    // A message rejected without requeue goes through the default exchange to the dead-letter queue (SDD §4.4).
    public static Dictionary<string, object?> QueueArguments(string queue) => new()
    {
        ["x-dead-letter-exchange"] = string.Empty,
        ["x-dead-letter-routing-key"] = DeadLetterQueueFor(queue),
    };
}
