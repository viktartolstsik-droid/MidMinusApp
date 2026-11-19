using System.Text.Json;

namespace Ingestor;

public enum PollOutcome
{
    Success,
    MalformedBody,
    Unauthorized,
    RateLimited,
    ServerError,
    UnexpectedStatus,
    Timeout,
    Unreachable,
    CircuitOpen,
}

public sealed record PollResult(PollOutcome Outcome, int? StatusCode, IReadOnlyList<JsonElement> Meters)
{
    public bool IsSuccess => Outcome == PollOutcome.Success;

    public static PollResult Success(IReadOnlyList<JsonElement> meters) => new(PollOutcome.Success, 200, meters);

    public static PollResult Failed(PollOutcome outcome, int? statusCode = null) => new(outcome, statusCode, []);
}
