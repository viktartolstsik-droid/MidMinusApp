using System.Net;
using Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Ingestor.Tests;

public sealed class PollingWorkerTests
{
    private const string ThreeMeters = """
        [
          { "type": "energy", "name": "Kitchen", "payload": { "energy": 482.13 } },
          { "type": "air_quality", "name": "Kitchen", "payload": { "co2": 640, "pm25": 12, "humidity": 41 } },
          { "type": "motion", "name": "Kitchen", "payload": { "motionDetected": true } }
        ]
        """;

    [Fact]
    public async Task Success_PublishesEveryMappedMessageWithOneIngestedAt()
    {
        var publisher = new FakePublisher();
        using var provider = BuildProvider(new StubHandler(StubHandler.Json(ThreeMeters)), publisher);

        await PollOnceAsync(provider);

        var batch = Assert.Single(publisher.Batches);
        Assert.Equal(
            ["Kitchen:energy", "Kitchen:air_quality.co2", "Kitchen:air_quality.pm25", "Kitchen:air_quality.humidity", "Kitchen:motion"],
            batch.Select(message => message.SourceId));
        Assert.Single(batch.Select(message => message.IngestedAt).Distinct());
    }

    [Fact]
    public async Task BadMeter_IsSkippedAndTheRestIsPublished()
    {
        var publisher = new FakePublisher();
        var body = """
            [
              { "type": "energy", "name": "Kitchen", "payload": { "energy": 1.5 } },
              { "type": "unknown", "name": "Garage", "payload": {} }
            ]
            """;
        using var provider = BuildProvider(new StubHandler(StubHandler.Json(body)), publisher);

        await PollOnceAsync(provider);

        var message = Assert.Single(Assert.Single(publisher.Batches));
        Assert.Equal("Kitchen:energy", message.SourceId);
    }

    [Fact]
    public async Task EmptyArray_PublishesNothing()
    {
        var publisher = new FakePublisher();
        using var provider = BuildProvider(new StubHandler(StubHandler.Json("[]")), publisher);

        await PollOnceAsync(provider);

        Assert.Equal(0, publisher.Calls);
    }

    [Fact]
    public async Task MalformedBody_PublishesNothing()
    {
        var publisher = new FakePublisher();
        using var provider = BuildProvider(new StubHandler(StubHandler.Json("""{"error":"data corrupted"}[]""")), publisher);

        await PollOnceAsync(provider);

        Assert.Equal(0, publisher.Calls);
    }

    [Fact]
    public async Task Unauthorized_PublishesNothing()
    {
        var publisher = new FakePublisher();
        using var provider = BuildProvider(new StubHandler(StubHandler.Status(HttpStatusCode.Unauthorized)), publisher);

        await PollOnceAsync(provider);

        Assert.Equal(0, publisher.Calls);
    }

    [Fact]
    public async Task PublishFailure_DoesNotThrowAndNextCycleRetries()
    {
        var publisher = new FakePublisher(failures: 1);
        using var provider = BuildProvider(new StubHandler(StubHandler.Json(ThreeMeters)), publisher);
        var worker = CreateWorker(provider);

        await worker.PollOnceAsync(1, CancellationToken.None);
        await worker.PollOnceAsync(2, CancellationToken.None);

        Assert.Equal(2, publisher.Calls);
        Assert.Equal(5, Assert.Single(publisher.Batches).Count);
    }

    private static Task PollOnceAsync(IServiceProvider provider) =>
        CreateWorker(provider).PollOnceAsync(1, CancellationToken.None);

    private static PollingWorker CreateWorker(IServiceProvider provider) =>
        ActivatorUtilities.CreateInstance<PollingWorker>(provider);

    private static ServiceProvider BuildProvider(StubHandler handler, FakePublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<PollingOptions>();
        services.AddOptions<WeakAppOptions>().Configure(options =>
        {
            options.BaseUrl = new Uri("http://weakapp:8080");
            options.ApiKey = "test-key";
        });
        services.AddWeakAppClient(retryDelay: TimeSpan.FromMilliseconds(1))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<IReadingPublisher>(publisher);
        return services.BuildServiceProvider();
    }

    private sealed class FakePublisher(int failures = 0) : IReadingPublisher
    {
        private readonly List<IReadOnlyList<RawReadingMessage>> _batches = [];

        public int Calls { get; private set; }

        public IReadOnlyList<IReadOnlyList<RawReadingMessage>> Batches => _batches;

        public Task PublishAsync(IReadOnlyList<RawReadingMessage> messages, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= failures)
            {
                throw new IOException("broker unreachable");
            }

            _batches.Add(messages);
            return Task.CompletedTask;
        }
    }
}
