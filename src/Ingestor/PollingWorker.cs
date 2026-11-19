using Microsoft.Extensions.Options;

namespace Ingestor;

public sealed class PollingWorker(
    IServiceScopeFactory scopeFactory,
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
            await PollAsync(cycle, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(long cycle, CancellationToken stoppingToken)
    {
        try
        {
            // Typed HttpClient is resolved per cycle so the factory can rotate its handler.
            using var scope = scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<WeakAppClient>();

            var result = await client.GetMetersAsync(stoppingToken);

            if (result.IsSuccess)
            {
                logger.LogInformation("Poll cycle {Cycle}: received {MeterCount} meters", cycle, result.Meters.Count);
            }
            else
            {
                logger.LogWarning(
                    "Poll cycle {Cycle} failed: {Outcome}, HTTP status {StatusCode}",
                    cycle, result.Outcome, result.StatusCode);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Poll cycle {Cycle} failed unexpectedly", cycle);
        }
    }
}
