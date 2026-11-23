using System.Text.Json;

namespace Ingestor.Tests;

public sealed class ReadingMapperTests
{
    private static readonly DateTimeOffset IngestedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Rooms = ["Kitchen", "Living Room", "Bedroom", "Garage", "Office", "Corridor"];

    [Fact]
    public void Energy_IsOneMessage()
    {
        var meter = Parse("""{ "type": "energy", "name": "Kitchen", "payload": { "energy": 482.13 } }""");

        var message = Assert.Single(ReadingMapper.MapMeter(meter, IngestedAt));

        Assert.Equal("Kitchen:energy", message.SourceId);
        Assert.Equal("Kitchen", message.Location);
        Assert.Equal("energy", message.Type);
        Assert.Equal(482.13m, message.Value);
        Assert.Equal(IngestedAt, message.IngestedAt);
        Assert.Null(message.Unit);
        Assert.NotEqual(Guid.Empty, message.MessageId);
        Assert.Equal(meter.GetRawText(), message.RawPayload?.GetRawText());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Motion_IsOneOrZero(bool detected, int expected)
    {
        var meter = Parse($$"""{ "type": "motion", "name": "Garage", "payload": { "motionDetected": {{(detected ? "true" : "false")}} } }""");

        var message = Assert.Single(ReadingMapper.MapMeter(meter, IngestedAt));

        Assert.Equal("Garage:motion", message.SourceId);
        Assert.Equal("motion", message.Type);
        Assert.Equal(expected, message.Value);
    }

    [Fact]
    public void AirQuality_FansOutIntoThreeMessages()
    {
        var meter = Parse("""{ "type": "air_quality", "name": "Living Room", "payload": { "co2": 640, "pm25": 12, "humidity": 41 } }""");

        var messages = ReadingMapper.MapMeter(meter, IngestedAt);

        Assert.Equal(
            [
                ("Living Room:air_quality.co2", "air_quality.co2", 640m),
                ("Living Room:air_quality.pm25", "air_quality.pm25", 12m),
                ("Living Room:air_quality.humidity", "air_quality.humidity", 41m),
            ],
            messages.Select(m => (m.SourceId, m.Type, m.Value)));
        Assert.All(messages, m => Assert.Equal("Living Room", m.Location));
        Assert.All(messages, m => Assert.Equal(meter.GetRawText(), m.RawPayload?.GetRawText()));
        Assert.Equal(3, messages.Select(m => m.MessageId).Distinct().Count());
    }

    [Theory]
    [InlineData("""{ "type": "temperature", "name": "Kitchen", "payload": { "temperature": 21 } }""")]
    [InlineData("""{ "type": "energy", "payload": { "energy": 1 } }""")]
    [InlineData("""{ "type": "energy", "name": "  ", "payload": { "energy": 1 } }""")]
    [InlineData("""{ "name": "Kitchen", "payload": { "energy": 1 } }""")]
    [InlineData("""{ "type": "energy", "name": "Kitchen" }""")]
    [InlineData("""{ "type": "energy", "name": "Kitchen", "payload": null }""")]
    [InlineData("""{ "type": "energy", "name": "Kitchen", "payload": {} }""")]
    [InlineData("""{ "type": "energy", "name": "Kitchen", "payload": { "energy": "482" } }""")]
    [InlineData("""{ "type": "motion", "name": "Kitchen", "payload": { "motionDetected": 1 } }""")]
    [InlineData("""{ "type": "air_quality", "name": "Kitchen", "payload": { "co2": 640, "humidity": 41 } }""")]
    [InlineData("""{ "type": "air_quality", "name": "Kitchen", "payload": { "co2": 640, "pm25": null, "humidity": 41 } }""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""null""")]
    public void BrokenMeter_IsSkipped(string json)
    {
        Assert.Empty(ReadingMapper.MapMeter(Parse(json), IngestedAt));
    }

    [Fact]
    public void BrokenMeterInTheMiddle_KeepsTheRest()
    {
        var meters = ParseArray("""
            [
              { "type": "energy", "name": "Kitchen", "payload": { "energy": 482.13 } },
              { "type": "air_quality", "name": "Kitchen", "payload": { "co2": 640 } },
              { "type": "motion", "name": "Kitchen", "payload": { "motionDetected": true } }
            ]
            """);

        var result = ReadingMapper.Map(meters, IngestedAt);

        Assert.Equal(["Kitchen:energy", "Kitchen:motion"], result.Messages.Select(m => m.SourceId));
        Assert.Equal(1, result.SkippedMeters);
    }

    [Theory]
    [InlineData("""{ "type": "energy", "name": "Office", "payload": { "energy": 1e400 } }""")]
    [InlineData("""{ "type": "energy", "name": "Office", "payload": { "energy": -1e400 } }""")]
    [InlineData("""{ "type": "energy", "name": "Office", "payload": { "energy": 79228162514264337593543950336 } }""")]
    [InlineData("""{ "type": "air_quality", "name": "Office", "payload": { "co2": 1e309, "pm25": 1, "humidity": 1 } }""")]
    [InlineData("""{ "type": 5, "name": ["Office"], "payload": "x" }""")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    public void ExtremeMeterInTheMiddle_IsSkippedAndKeepsTheRest(string json)
    {
        var meters = new[]
        {
            Parse("""{ "type": "energy", "name": "Kitchen", "payload": { "energy": 1 } }"""),
            Parse(json),
            Parse("""{ "type": "motion", "name": "Kitchen", "payload": { "motionDetected": true } }"""),
        };

        var result = ReadingMapper.Map(meters, IngestedAt);

        Assert.Equal(["Kitchen:energy", "Kitchen:motion"], result.Messages.Select(m => m.SourceId));
        Assert.Equal(1, result.SkippedMeters);
    }

    [Fact]
    public void DefaultElement_IsSkipped()
    {
        var result = ReadingMapper.Map([default], IngestedAt);

        Assert.Empty(result.Messages);
        Assert.Equal(1, result.SkippedMeters);
    }

    [Fact]
    public void DuplicateKeys_DoNotBreakThePoll()
    {
        var meters = new[]
        {
            Parse("""{ "type": "energy", "name": "Kitchen", "name": "Office", "payload": { "energy": 1, "energy": 2 } }"""),
            Parse("""{ "type": "motion", "name": "Kitchen", "payload": { "motionDetected": true } }"""),
        };

        var result = ReadingMapper.Map(meters, IngestedAt);

        Assert.Contains(result.Messages, m => m.SourceId == "Kitchen:motion");
    }

    [Fact]
    public void FullSnapshot_GivesThirtyMessagesWithOneIngestedAt()
    {
        var meters = Rooms
            .SelectMany(room => new[]
            {
                $$"""{ "type": "energy", "name": "{{room}}", "payload": { "energy": 100.5 } }""",
                $$"""{ "type": "air_quality", "name": "{{room}}", "payload": { "co2": 500, "pm25": 20, "humidity": 50 } }""",
                $$"""{ "type": "motion", "name": "{{room}}", "payload": { "motionDetected": false } }""",
            })
            .Select(Parse)
            .ToList();

        var result = ReadingMapper.Map(meters, IngestedAt);

        Assert.Equal(18, meters.Count);
        Assert.Equal(30, result.Messages.Count);
        Assert.Equal(0, result.SkippedMeters);
        Assert.Equal(30, result.Messages.Select(m => m.SourceId).Distinct().Count());
        Assert.Equal(30, result.Messages.Select(m => m.MessageId).Distinct().Count());
        Assert.All(result.Messages, m => Assert.Equal(IngestedAt, m.IngestedAt));
        Assert.Equal(
            ["air_quality.co2", "air_quality.humidity", "air_quality.pm25", "energy", "motion"],
            result.Messages.Select(m => m.Type).Distinct().Order());
        Assert.Contains(result.Messages, m => m.SourceId == "Living Room:air_quality.pm25");
    }

    [Fact]
    public void EmptyArray_GivesNoMessages()
    {
        var result = ReadingMapper.Map([], IngestedAt);

        Assert.Empty(result.Messages);
        Assert.Equal(0, result.SkippedMeters);
    }

    [Fact]
    public void IngestedAt_IsStoredInUtc()
    {
        var local = new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.FromHours(3));
        var meter = Parse("""{ "type": "energy", "name": "Office", "payload": { "energy": 1 } }""");

        var message = Assert.Single(ReadingMapper.MapMeter(meter, local));

        Assert.Equal(TimeSpan.Zero, message.IngestedAt.Offset);
        Assert.Equal(local, message.IngestedAt);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static List<JsonElement> ParseArray(string json) => Parse(json).EnumerateArray().ToList();
}
