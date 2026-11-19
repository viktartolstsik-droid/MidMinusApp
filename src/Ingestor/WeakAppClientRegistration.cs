using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Ingestor;

public static class WeakAppClientRegistration
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);

    public static IHttpClientBuilder AddWeakAppClient(this IServiceCollection services, TimeSpan? retryDelay = null)
    {
        var builder = services.AddHttpClient<WeakAppClient>((provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<WeakAppOptions>>().Value;
            http.BaseAddress = options.BaseUrl;
            // Each attempt is bounded by the resilience timeout; a client-wide timeout would also cut retries short.
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        });

        builder.AddResilienceHandler("weakapp", (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<WeakAppOptions>>().Value;

            pipeline
                // A rejected request still takes a slot in WeakApp's 10 per 40 s limit, so retry only once and only when told how long to wait.
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 1,
                    ShouldRetryAfterHeader = true,
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests, Headers.RetryAfter: not null }),
                })
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = retryDelay ?? DefaultRetryDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = false,
                    ShouldRetryAfterHeader = false,
                    ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
                })
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 1.0,
                    MinimumThroughput = 5,
                    SamplingDuration = TimeSpan.FromSeconds(60),
                    BreakDuration = TimeSpan.FromSeconds(30),
                    ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
                })
                .AddTimeout(options.Timeout);
        });

        return builder;
    }

    private static bool IsTransient(Outcome<HttpResponseMessage> outcome) => outcome switch
    {
        { Exception: HttpRequestException or TimeoutRejectedException } => true,
        { Result.StatusCode: HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout } => true,
        _ => false,
    };
}
