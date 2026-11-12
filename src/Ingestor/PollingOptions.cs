namespace Ingestor;

public sealed class PollingOptions
{
    public const string SectionName = "Polling";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);
}
