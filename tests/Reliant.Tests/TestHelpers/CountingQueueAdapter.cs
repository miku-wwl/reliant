using Reliant.Application.Abstractions;

namespace Reliant.Tests.TestHelpers;

/// <summary>
/// Decorates a real queue adapter and counts message operations so tests can
/// assert real redelivery semantics (receive count, delete/ack count, send count).
/// </summary>
public sealed class CountingQueueAdapter : IQueueAdapter
{
    private readonly IQueueAdapter _inner;
    private int _receiveCount;
    private int _visibilityRenewalCount;
    private int _deleteCount;
    private int _sendCount;

    public CountingQueueAdapter(IQueueAdapter inner)
    {
        _inner = inner;
    }

    public int ReceiveCount => _receiveCount;
    public int VisibilityRenewalCount => _visibilityRenewalCount;
    public int DeleteCount => _deleteCount;
    public int SendCount => _sendCount;

    public Task<string> GetOrCreateQueueAsync(string queueName, CancellationToken cancellationToken = default)
        => _inner.GetOrCreateQueueAsync(queueName, cancellationToken);

    public async Task<IQueueMessage?> ReceiveAsync(string queueUrl, int visibilityTimeoutSeconds, CancellationToken cancellationToken = default)
    {
        var message = await _inner.ReceiveAsync(queueUrl, visibilityTimeoutSeconds, cancellationToken);
        if (message is not null)
            Interlocked.Increment(ref _receiveCount);
        return message;
    }

    public Task DeleteAsync(string queueUrl, string receiptHandle, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _deleteCount);
        return _inner.DeleteAsync(queueUrl, receiptHandle, cancellationToken);
    }

    public Task RenewVisibilityAsync(
        string queueUrl,
        string receiptHandle,
        int visibilityTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _visibilityRenewalCount);
        return _inner.RenewVisibilityAsync(
            queueUrl,
            receiptHandle,
            visibilityTimeoutSeconds,
            cancellationToken);
    }

    public Task SendAsync(string queueUrl, string messageBody, string messageId, string messageType, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _sendCount);
        return _inner.SendAsync(queueUrl, messageBody, messageId, messageType, cancellationToken);
    }
}
