using System.Text.Json;

namespace Contracts;

public static class RawReadingTransport
{
    public const string QueueName = "ingestor.raw";

    public const string ContentType = "application/json";

    // Camel case matches the field names of RawReadingMessage in SDD §4.1.
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
