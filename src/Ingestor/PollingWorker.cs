using Microsoft.Extensions.Options;

namespace Ingestor;

public sealed class PollingWorker(
    IOptions<PollingOptions> pollingOptions,
    IOptions<WeakAppOptions> weakAppOptions,
    ILogger<PollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = pollingOptions.Value.Interval;
        var weakApp = weakAppOptions.Value;

        logger.LogInformation(
            "Polling {BaseUrl} every {Interval} with timeout {Timeout}",
            weakApp.BaseUrl, interval, weakApp.Timeout);

        using var timer = new PeriodicTimer(interval);
        var cycle = 0L;

        do
        {
            cycle++;
            logger.LogInformation("Poll cycle {Cycle}: stub, no request sent", cycle);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
