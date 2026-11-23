using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ingestor;

public sealed class RabbitMqHealthCheck(RabbitMqPublisher publisher) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await publisher.IsConnectedAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("RabbitMQ channel is closed.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ is unreachable.", ex);
        }
    }
}
