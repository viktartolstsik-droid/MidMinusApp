using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Ingestor.Tests;

public sealed class WeakAppClientTests
{
    private const string ThreeMeters = """
        [
          { "type": "energy", "name": "Kitchen", "payload": { "energy": 482.13 } },
          { "type": "air_quality", "name": "Kitchen", "payload": { "co2": 640, "pm25": 12, "humidity": 41 } },
          { "type": "motion", "name": "Kitchen", "payload": { "motionDetected": true } }
        ]
        """;

    [Fact]
    public async Task Array_IsSuccessWithEveryMeter()
    {
        var handler = new StubHandler(StubHandler.Json(ThreeMeters));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Meters.Count);
        Assert.Equal("air_quality", result.Meters[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Request_GoesToMetersWithApiKey()
    {
        var handler = new StubHandler(StubHandler.Json("[]"));

        await PollAsync(handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("http://weakapp:8080/meters"), request.RequestUri);
        Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task EmptyArray_IsSuccessWithoutMeters()
    {
        var handler = new StubHandler(StubHandler.Json("[]"));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.Success, result.Outcome);
        Assert.Empty(result.Meters);
    }

    [Theory]
    [InlineData("""{"error":"data corrupted"}""")]
    [InlineData("""{"error":"data corrupted"}[]""")]
    [InlineData("""[{"type":"energy",""")]
    [InlineData("")]
    public async Task OkWithoutArray_IsMalformedAndNotRetried(string body)
    {
        var handler = new StubHandler(StubHandler.Json(body));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.MalformedBody, result.Outcome);
        Assert.Empty(result.Meters);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_IsNotRetried()
    {
        var handler = new StubHandler(StubHandler.Status(HttpStatusCode.Unauthorized));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.Unauthorized, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TooManyRequests_WaitsRetryAfterThenSucceeds()
    {
        var handler = new StubHandler(
            StubHandler.TooManyRequests(TimeSpan.FromSeconds(1)),
            StubHandler.Json(ThreeMeters));
        var started = TimeProvider.System.GetTimestamp();

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.Success, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(TimeProvider.System.GetElapsedTime(started) >= TimeSpan.FromSeconds(0.9));
    }

    [Fact]
    public async Task TooManyRequests_IsRetriedOnlyOnce()
    {
        var handler = new StubHandler(StubHandler.TooManyRequests(TimeSpan.FromSeconds(1)));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.RateLimited, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TooManyRequestsWithoutRetryAfter_IsNotRetried()
    {
        var handler = new StubHandler(StubHandler.TooManyRequests(retryAfter: null));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.RateLimited, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BadGatewayThenOk_IsSuccess()
    {
        var handler = new StubHandler(
            StubHandler.Status(HttpStatusCode.BadGateway),
            StubHandler.Json(ThreeMeters));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Meters.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ServerError_IsRetriedTwiceThenFails(HttpStatusCode status)
    {
        var handler = new StubHandler(StubHandler.Status(status));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.ServerError, result.Outcome);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SlowResponse_TimesOutAndIsRetried()
    {
        var handler = new StubHandler(StubHandler.Hang());

        var result = await PollAsync(handler, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal(PollOutcome.Timeout, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task UnexpectedStatus_IsNotRetried()
    {
        var handler = new StubHandler(StubHandler.Status(HttpStatusCode.NotFound));

        var result = await PollAsync(handler);

        Assert.Equal(PollOutcome.UnexpectedStatus, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FiveFailedAttempts_OpenCircuitAndStopCallingWeakApp()
    {
        var handler = new StubHandler(StubHandler.Status(HttpStatusCode.BadGateway));
        using var provider = BuildProvider(handler);

        await PollAsync(provider);
        await PollAsync(provider);
        var result = await PollAsync(provider);

        Assert.Equal(PollOutcome.CircuitOpen, result.Outcome);
        Assert.Equal(5, handler.Requests.Count);
    }

    private static async Task<PollResult> PollAsync(StubHandler handler, TimeSpan? timeout = null)
    {
        using var provider = BuildProvider(handler, timeout);
        return await PollAsync(provider);
    }

    private static Task<PollResult> PollAsync(IServiceProvider provider) =>
        provider.GetRequiredService<WeakAppClient>().GetMetersAsync(CancellationToken.None);

    private static ServiceProvider BuildProvider(StubHandler handler, TimeSpan? timeout = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<WeakAppOptions>().Configure(options =>
        {
            options.BaseUrl = new Uri("http://weakapp:8080");
            options.ApiKey = "test-key";
            options.Timeout = timeout ?? WeakAppOptions.MinTimeout;
        });
        services.AddWeakAppClient(retryDelay: TimeSpan.FromMilliseconds(1))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }
}
