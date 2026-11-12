using Ingestor;

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

builder.Services.AddHostedService<PollingWorker>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();
