using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ingestor;

public sealed class WeakAppClient(HttpClient http)
{
    public const string MetersPath = "meters";

    public async Task<PollResult> GetMetersAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(MetersPath, cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            return PollResult.Failed(PollOutcome.CircuitOpen);
        }
        catch (TimeoutRejectedException)
        {
            return PollResult.Failed(PollOutcome.Timeout);
        }
        catch (HttpRequestException)
        {
            return PollResult.Failed(PollOutcome.Unreachable);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status == 200)
            {
                return await ReadMetersAsync(response.Content, cancellationToken);
            }

            var outcome = status switch
            {
                401 => PollOutcome.Unauthorized,
                429 => PollOutcome.RateLimited,
                >= 500 => PollOutcome.ServerError,
                _ => PollOutcome.UnexpectedStatus,
            };
            return PollResult.Failed(outcome, status);
        }
    }

    // WeakApp sometimes answers 200 with `{"error":"data corrupted"}` glued to an array, so only a well-formed array counts.
    private static async Task<PollResult> ReadMetersAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return PollResult.Failed(PollOutcome.MalformedBody, 200);
            }

            var meters = document.RootElement.EnumerateArray().Select(meter => meter.Clone()).ToList();
            return PollResult.Success(meters);
        }
        catch (JsonException)
        {
            return PollResult.Failed(PollOutcome.MalformedBody, 200);
        }
    }
}
