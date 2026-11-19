using System.Text.Json;

namespace Contracts;

public sealed record RawReadingMessage(
    Guid MessageId,
    DateTimeOffset IngestedAt,
    string SourceId,
    string Location,
    string Type,
    decimal Value,
    string? Unit,
    JsonElement? RawPayload);
