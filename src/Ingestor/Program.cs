using Ingestor;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<WeakAppOptions>()
    .Bind(builder.Configuration.GetSection(WeakAppOptions.SectionName))
    .Validate(o => o.BaseUrl is { IsAbsoluteUri: true }, "WeakApp:BaseUrl must be an absolute URL.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "WeakApp:ApiKey is required.")
    .Validate(o => o.Timeout >= WeakAppOptions.MinTimeout, $"WeakApp:Timeout must be at least {WeakAppOptions.MinTimeout}.")
    .ValidateOnStart();

builder.Services.AddOptions<PollingOptions>()
    .Bind(builder.Configuration.GetSection(PollingOptions.SectionName))
    .Validate(o => o.Interval > TimeSpan.Zero, "Polling:Interval must be positive.")
    .ValidateOnStart();

builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "RabbitMQ:Host is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.UserName), "RabbitMQ:UserName is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Password), "RabbitMQ:Password is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Queue), "RabbitMQ:Queue is required.")
    .Validate(o => o.ConnectTimeout > TimeSpan.Zero, "RabbitMQ:ConnectTimeout must be positive.")
    .ValidateOnStart();

builder.Services.AddWeakAppClient();
builder.Services.AddRabbitMqPublisher();
builder.Services.AddHostedService<PollingWorker>();

var app = builder.Build();

// Liveness must not depend on RabbitMQ, otherwise a broker outage restarts a healthy poller.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(RabbitMqRegistration.ReadyTag),
});

app.Run();
