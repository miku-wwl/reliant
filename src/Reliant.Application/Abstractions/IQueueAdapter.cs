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

public enum QueueVisibilityFailureKind
{
    InvalidReceiptHandle = 1,
    RateLimited = 2,
    Timeout = 3,
    TransientServiceFailure = 4,
    PermanentFailure = 5
}

public sealed class QueueVisibilityRenewalException(
    QueueVisibilityFailureKind failureKind,
    bool isTransient,
    string message,
    Exception innerException) : Exception(message, innerException)
{
    public QueueVisibilityFailureKind FailureKind { get; } =
        failureKind;
    public bool IsTransient { get; } = isTransient;
}

public interface IQueueMessage
{
    string MessageId { get; }
    string MessageType { get; }
    string Payload { get; }
    int ApproximateReceiveCount { get; }
    string ReceiptHandle { get; }
    string PhysicalMessageId => MessageId;
    string? CorrelationId => null;
    string? CausationId => null;
    string? TraceParent => null;
    string? TraceState => null;
    string? DeploymentVersion => null;
    DateTimeOffset? SentAt => null;
}

public sealed record QueueMessageTelemetryContext(
    string? CorrelationId,
    string? CausationId,
    string? TraceParent,
    string? TraceState,
    string? DeploymentVersion);

public sealed record QueueMetricsSnapshot(
    long VisibleMessages,
    long InFlightMessages,
    long DelayedMessages);

public interface IQueueAdapter
{
    Task<string> GetOrCreateQueueAsync(string queueName, CancellationToken cancellationToken = default);
    Task<IQueueMessage?> ReceiveAsync(string queueUrl, int visibilityTimeoutSeconds, CancellationToken cancellationToken = default);
    Task RenewVisibilityAsync(string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken cancellationToken = default);
    Task DeleteAsync(string queueUrl, string receiptHandle, CancellationToken cancellationToken = default);
    Task SendAsync(string queueUrl, string messageBody, string messageId, string messageType, CancellationToken cancellationToken = default);

    Task SendAsync(
        string queueUrl,
        string messageBody,
        string messageId,
        string messageType,
        QueueMessageTelemetryContext telemetryContext,
        CancellationToken cancellationToken = default)
        => SendAsync(
            queueUrl,
            messageBody,
            messageId,
            messageType,
            cancellationToken);

    Task<QueueMetricsSnapshot?> GetMetricsAsync(
        string queueUrl,
        CancellationToken cancellationToken = default)
        => Task.FromResult<QueueMetricsSnapshot?>(null);
}

public interface IQueueMessagePublisher
{
    Task PublishAsync(string queueName, string messageType, string payload, string messageId, CancellationToken cancellationToken = default);

    Task PublishAsync(
        string queueName,
        string messageType,
        string payload,
        string messageId,
        QueueMessageTelemetryContext telemetryContext,
        CancellationToken cancellationToken = default)
        => PublishAsync(
            queueName,
            messageType,
            payload,
            messageId,
            cancellationToken);
}
