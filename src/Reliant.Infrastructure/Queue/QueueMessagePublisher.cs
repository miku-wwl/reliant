using Reliant.Application.Abstractions;

namespace Reliant.Infrastructure.Queue;

public class QueueMessagePublisher(IQueueAdapter queueAdapter) : IQueueMessagePublisher
{
    public async Task PublishAsync(string queueName, string messageType, string payload, string messageId, CancellationToken cancellationToken = default)
    {
        var queueUrl = await queueAdapter.GetOrCreateQueueAsync(queueName, cancellationToken);
        await queueAdapter.SendAsync(queueUrl, payload, messageId, messageType, cancellationToken);
    }
}
