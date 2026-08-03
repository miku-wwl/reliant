using Reliant.Domain.Enums;

namespace Reliant.Application.Abstractions;

public sealed class QueuePublishException(
    ErrorCategory errorCategory,
    bool isTransient,
    string message,
    Exception innerException) : Exception(message, innerException)
{
    public ErrorCategory ErrorCategory { get; } = errorCategory;
    public bool IsTransient { get; } = isTransient;
}

public interface IQueueMessage
{
    string MessageId { get; }
    string MessageType { get; }
    string Payload { get; }
    int ApproximateReceiveCount { get; }
    string ReceiptHandle { get; }
}

public interface IQueueAdapter
{
    Task<string> GetOrCreateQueueAsync(string queueName, CancellationToken cancellationToken = default);
    Task<IQueueMessage?> ReceiveAsync(string queueUrl, int visibilityTimeoutSeconds, CancellationToken cancellationToken = default);
    Task DeleteAsync(string queueUrl, string receiptHandle, CancellationToken cancellationToken = default);
    Task SendAsync(string queueUrl, string messageBody, string messageId, string messageType, CancellationToken cancellationToken = default);
}

public interface IQueueMessagePublisher
{
    Task PublishAsync(string queueName, string messageType, string payload, string messageId, CancellationToken cancellationToken = default);
}
