namespace Ingestor;

public static class RabbitMqRegistration
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddRabbitMqPublisher(this IServiceCollection services)
    {
        services.AddSingleton<RabbitMqPublisher>();
        services.AddSingleton<IReadingPublisher>(provider => provider.GetRequiredService<RabbitMqPublisher>());
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: [ReadyTag]);
        return services;
    }
}
