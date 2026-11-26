using Contracts;

namespace Ingestor;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Queue { get; set; } = RawReadingTransport.QueueName;

    // Bounds both /ready and a poll cycle while the broker is down.
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
