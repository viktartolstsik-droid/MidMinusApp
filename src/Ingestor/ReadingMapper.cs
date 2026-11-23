using System.Text.Json;
using Contracts;

namespace Ingestor;

public sealed record MapResult(IReadOnlyList<RawReadingMessage> Messages, int SkippedMeters);

public static class ReadingMapper
{
    public static MapResult Map(IEnumerable<JsonElement> meters, DateTimeOffset ingestedAt)
    {
        var messages = new List<RawReadingMessage>();
        var skipped = 0;

        foreach (var meter in meters)
        {
            IReadOnlyList<RawReadingMessage> mapped;
            try
            {
                mapped = MapMeter(meter, ingestedAt);
            }
            catch (Exception)
            {
                // MapMeter is written not to throw on any JSON; this keeps a future slip from costing the whole poll.
                mapped = [];
            }

            if (mapped.Count == 0)
            {
                skipped++;
            }

            messages.AddRange(mapped);
        }

        return new MapResult(messages, skipped);
    }

    // Returns no messages when the meter has to be skipped; a single bad meter must not cost the rest of the poll.
    public static IReadOnlyList<RawReadingMessage> MapMeter(JsonElement meter, DateTimeOffset ingestedAt)
    {
        if (meter.ValueKind != JsonValueKind.Object
            || !TryGetString(meter, "name", out var location)
            || !TryGetString(meter, "type", out var type)
            || !meter.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var readings = type switch
        {
            "energy" => MapEnergy(payload),
            "motion" => MapMotion(payload),
            "air_quality" => MapAirQuality(payload),
            _ => null,
        };

        if (readings is null)
        {
            return [];
        }

        var rawPayload = meter.Clone();
        var utc = ingestedAt.ToUniversalTime();

        return readings
            .Select(reading => new RawReadingMessage(
                MessageId: Guid.NewGuid(),
                IngestedAt: utc,
                SourceId: $"{location}:{reading.Type}",
                Location: location,
                Type: reading.Type,
                Value: reading.Value,
                Unit: null,
                RawPayload: rawPayload))
            .ToList();
    }

    private static (string Type, decimal Value)[]? MapEnergy(JsonElement payload) =>
        TryGetNumber(payload, "energy", out var energy) ? [("energy", energy)] : null;

    private static (string Type, decimal Value)[]? MapMotion(JsonElement payload)
    {
        if (!payload.TryGetProperty("motionDetected", out var detected)
            || detected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return [("motion", detected.GetBoolean() ? 1m : 0m)];
    }

    private static (string Type, decimal Value)[]? MapAirQuality(JsonElement payload)
    {
        if (!TryGetNumber(payload, "co2", out var co2)
            || !TryGetNumber(payload, "pm25", out var pm25)
            || !TryGetNumber(payload, "humidity", out var humidity))
        {
            return null;
        }

        return
        [
            ("air_quality.co2", co2),
            ("air_quality.pm25", pm25),
            ("air_quality.humidity", humidity),
        ];
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetNumber(JsonElement element, string name, out decimal value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDecimal(out value);
    }
}
