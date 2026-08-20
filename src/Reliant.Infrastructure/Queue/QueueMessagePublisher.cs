using Amazon.Runtime;
using Reliant.Application.Abstractions;
using Reliant.Application.Observability;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Observability;
using System.Diagnostics;
using System.Net;

namespace Reliant.Infrastructure.Queue;

public class QueueMessagePublisher(
    IQueueAdapter queueAdapter,
    DeploymentInfo? deploymentInfo = null) : IQueueMessagePublisher
{
    public async Task PublishAsync(string queueName, string messageType, string payload, string messageId, CancellationToken cancellationToken = default)
        => await PublishCoreAsync(
            queueName,
            messageType,
            payload,
            messageId,
            null,
            cancellationToken);

    public async Task PublishAsync(
        string queueName,
        string messageType,
        string payload,
        string messageId,
        QueueMessageTelemetryContext telemetryContext,
        CancellationToken cancellationToken = default)
        => await PublishCoreAsync(
            queueName,
            messageType,
            payload,
            messageId,
            telemetryContext,
            cancellationToken);

    private async Task PublishCoreAsync(
        string queueName,
        string messageType,
        string payload,
        string messageId,
        QueueMessageTelemetryContext? telemetryContext,
        CancellationToken cancellationToken)
    {
        using var activity = telemetryContext?.TraceParent is not null &&
            Activity.Current is null
            ? ReliantTelemetry.StartActivity(
                $"{messageType} send",
                ActivityKind.Producer,
                telemetryContext.TraceParent,
                telemetryContext.TraceState)
            : ReliantTelemetry.StartQueueProducer(
                ReliantTelemetry.NormalizeQueue(queueName),
                messageType,
                messageId);
        activity?.SetTag("reliant.correlation_id", telemetryContext?.CorrelationId);
        activity?.SetTag("reliant.causation_id", telemetryContext?.CausationId);

        try
        {
            var queueUrl = await queueAdapter.GetOrCreateQueueAsync(
                queueName,
                cancellationToken);
            var propagatedContext = new QueueMessageTelemetryContext(
                telemetryContext?.CorrelationId ??
                    Activity.Current?.GetBaggageItem(
                        "reliant.correlation_id"),
                telemetryContext?.CausationId,
                Activity.Current?.Id ?? telemetryContext?.TraceParent,
                Activity.Current?.TraceStateString ??
                    telemetryContext?.TraceState,
                telemetryContext?.DeploymentVersion ??
                    deploymentInfo?.Version);
            await queueAdapter.SendAsync(
                queueUrl,
                payload,
                messageId,
                messageType,
                propagatedContext,
                cancellationToken);
            ReliantTelemetry.RecordQueuePublish(queueName, "success");
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (category, isTransient) = Classify(ex);
            ReliantTelemetry.RecordQueuePublish(queueName, "failure");
            activity?.SetStatus(ActivityStatusCode.Error, category.ToString());
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
