namespace Ingestor;

public sealed class WeakAppOptions
{
    public const string SectionName = "WeakApp";

    // WeakApp occasionally answers after 30-35 s; a shorter timeout cuts off a valid response.
    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(45);

    public Uri? BaseUrl { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = MinTimeout;
}
