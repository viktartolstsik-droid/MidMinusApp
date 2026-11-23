using System.Diagnostics;
using System.Net;
using System.Text;

namespace Ingestor.Tests;

public sealed class StubHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    public static Func<CancellationToken, Task<HttpResponseMessage>> Status(HttpStatusCode status) =>
        _ => Task.FromResult(new HttpResponseMessage(status));

    public static Func<CancellationToken, Task<HttpResponseMessage>> Json(string body) =>
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    public static Func<CancellationToken, Task<HttpResponseMessage>> TooManyRequests(TimeSpan? retryAfter)
    {
        return _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (retryAfter is { } delay)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            }
            return Task.FromResult(response);
        };
    }

    public static Func<CancellationToken, Task<HttpResponseMessage>> Hang() =>
        async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        };

    // The last response repeats once the list runs out.
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        var next = responses[Math.Min(_requests.Count, responses.Length) - 1];
        return next(cancellationToken);
    }
}
