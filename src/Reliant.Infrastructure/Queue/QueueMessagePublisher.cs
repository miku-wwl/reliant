using Amazon.Runtime;
using Reliant.Application.Abstractions;
using Reliant.Domain.Enums;
using System.Net;

namespace Reliant.Infrastructure.Queue;

public class QueueMessagePublisher(IQueueAdapter queueAdapter) : IQueueMessagePublisher
{
    public async Task PublishAsync(string queueName, string messageType, string payload, string messageId, CancellationToken cancellationToken = default)
    {
        try
        {
            var queueUrl = await queueAdapter.GetOrCreateQueueAsync(
                queueName,
                cancellationToken);
            await queueAdapter.SendAsync(
                queueUrl,
                payload,
                messageId,
                messageType,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (category, isTransient) = Classify(ex);
            throw new QueuePublishException(
                category,
                isTransient,
                $"Queue publish failed: {ex.Message}",
                ex);
        }
    }

    private static (ErrorCategory Category, bool IsTransient)
        Classify(Exception exception)
    {
        if (exception is AmazonServiceException serviceException)
        {
            return serviceException.StatusCode switch
            {
                HttpStatusCode.RequestTimeout =>
                    (ErrorCategory.Timeout, true),
                (HttpStatusCode)429 =>
                    (ErrorCategory.RateLimited, true),
                >= HttpStatusCode.InternalServerError =>
                    (ErrorCategory.ServerError, true),
                0 =>
                    (ErrorCategory.NetworkFailure, true),
                HttpStatusCode.Unauthorized or
                    HttpStatusCode.Forbidden =>
                    (ErrorCategory.AuthenticationFailure, false),
                _ =>
                    (ErrorCategory.PermanentBusinessRejection, false)
            };
        }

        if (exception is TimeoutException or TaskCanceledException)
        {
            return (ErrorCategory.Timeout, true);
        }

        if (exception is HttpRequestException or IOException)
        {
            return (ErrorCategory.NetworkFailure, true);
        }

        if (exception.InnerException is not null)
        {
            return Classify(exception.InnerException);
        }

        // Unknown transport failures remain recoverable. The Publisher still
        // applies bounded backoff, so this fail-safe does not create a hot loop.
        return (ErrorCategory.NetworkFailure, true);
    }
}
