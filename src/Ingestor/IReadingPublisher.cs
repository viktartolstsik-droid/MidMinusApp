using Contracts;

namespace Ingestor;

public interface IReadingPublisher
{
    Task PublishAsync(IReadOnlyList<RawReadingMessage> messages, CancellationToken cancellationToken);
}
