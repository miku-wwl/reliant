using Reliant.Application.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp1;

/// <summary>
/// Delegates every queue operation except SendAsync. SendAsync pauses at the
/// boundary immediately before the real broker send until the worker host is
/// stopped. This gives the publisher-crash lab a deterministic pre-publish
/// failure window without putting a test hook in production code.
/// </summary>
public sealed class PauseBeforeSendQueueAdapter(IQueueAdapter inner) : IQueueAdapter
{
    private readonly TaskCompletionSource _sendReached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sendAttempts;

    public int SendAttempts => _sendAttempts;

    public Task WaitUntilSendReachedAsync(TimeSpan timeout)
        => _sendReached.Task.WaitAsync(timeout);

    public Task<string> GetOrCreateQueueAsync(
        string queueName,
        CancellationToken cancellationToken = default)
        => inner.GetOrCreateQueueAsync(queueName, cancellationToken);

    public Task<IQueueMessage?> ReceiveAsync(
        string queueUrl,
        int visibilityTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => inner.ReceiveAsync(queueUrl, visibilityTimeoutSeconds, cancellationToken);

    public Task DeleteAsync(
        string queueUrl,
        string receiptHandle,
        CancellationToken cancellationToken = default)
        => inner.DeleteAsync(queueUrl, receiptHandle, cancellationToken);

    public Task RenewVisibilityAsync(
        string queueUrl,
        string receiptHandle,
        int visibilityTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => inner.RenewVisibilityAsync(
            queueUrl,
            receiptHandle,
            visibilityTimeoutSeconds,
            cancellationToken);

    public async Task SendAsync(
        string queueUrl,
        string messageBody,
        string messageId,
        string messageType,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _sendAttempts);
        _sendReached.TrySetResult();

        // The test stops the host while execution is paused here. Cancellation
        // proves the real adapter below was never called in the first run.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
