using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using Reliant.Application.Observability;
using Reliant.Infrastructure.Observability;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Reliant.Infrastructure.Queue;

public class SqsQueueAdapter : IQueueAdapter
{
    private readonly AmazonSQSClient _client;
    private readonly int _maxReceiveCount;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _publishTimeout;
    private readonly ConcurrentDictionary<string, string> _queueUrls =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queueProvisioningGate = new(1, 1);
    private readonly QueueAvailabilityState _availability;

    public SqsQueueAdapter(
        IConfiguration configuration,
        QueueAvailabilityState? availability = null)
    {
        _availability = availability ?? new QueueAvailabilityState();
        var endpoint = configuration["Queue:Endpoint"] ?? "http://localhost:4566";
        var region = configuration["Queue:Region"] ?? "us-west-1";
        var requestTimeoutSeconds =
            int.TryParse(
                configuration["Queue:RequestTimeoutSeconds"],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedRequestTimeoutSeconds)
                ? parsedRequestTimeoutSeconds
                : 5;
        var maxErrorRetry =
            int.TryParse(
                configuration["Queue:MaxErrorRetry"],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedMaxErrorRetry)
                ? parsedMaxErrorRetry
                : 1;
        var publishTimeoutSeconds =
            int.TryParse(
                configuration["Queue:PublishTimeoutSeconds"],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedPublishTimeoutSeconds)
                ? parsedPublishTimeoutSeconds
                : requestTimeoutSeconds;

        var config = new AmazonSQSConfig
        {
            ServiceURL = endpoint,
            AuthenticationRegion = region,
            MaxErrorRetry = Math.Max(0, maxErrorRetry)
        };

        if (endpoint.Contains("localhost") || endpoint.Contains("4566"))
        {
            config.ServiceURL = endpoint;
        }

        _client = new AmazonSQSClient("test", "test", config);
        _requestTimeout = TimeSpan.FromSeconds(
            Math.Max(1, requestTimeoutSeconds));
        _publishTimeout = TimeSpan.FromSeconds(
            Math.Max(1, publishTimeoutSeconds));
        var configuredMaxReceiveCount =
            int.TryParse(
                configuration["Queue:MaxReceiveCount"],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedMaxReceiveCount)
                ? parsedMaxReceiveCount
                : 5;
        _maxReceiveCount = Math.Max(
            1,
            configuredMaxReceiveCount);
    }

    public async Task<string> GetOrCreateQueueAsync(
        string queueName,
        CancellationToken cancellationToken = default)
    {
        if (_queueUrls.TryGetValue(queueName, out var cachedQueueUrl))
        {
            _availability.RecordSuccess();
            return cachedQueueUrl;
        }

        using var requestCts =
            CreateRequestCancellationTokenSource(
                cancellationToken,
                _requestTimeout);
        var requestToken = requestCts.Token;

        await _queueProvisioningGate.WaitAsync(requestToken);
        try
        {
            if (_queueUrls.TryGetValue(queueName, out cachedQueueUrl))
            {
                return cachedQueueUrl;
            }

            var deadLetterQueueUrl = await GetOrCreateRawQueueAsync(
                $"{queueName}-dlq",
                requestToken);
            var deadLetterAttributes =
                await _client.GetQueueAttributesAsync(
                    deadLetterQueueUrl,
                    [QueueAttributeName.QueueArn],
                    requestToken);
            if (string.IsNullOrWhiteSpace(deadLetterAttributes.QueueARN))
            {
                throw new InvalidOperationException(
                    $"SQS did not return an ARN for DLQ {queueName}-dlq");
            }

            var queueUrl = await GetOrCreateRawQueueAsync(
                queueName,
                requestToken);
            var redrivePolicy = JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["deadLetterTargetArn"] =
                        deadLetterAttributes.QueueARN,
                    ["maxReceiveCount"] =
                        _maxReceiveCount.ToString(
                            CultureInfo.InvariantCulture)
                });
            await _client.SetQueueAttributesAsync(
                queueUrl,
                new Dictionary<string, string>
                {
                    ["RedrivePolicy"] = redrivePolicy
                },
                requestToken);

            _queueUrls[queueName] = queueUrl;
            _availability.RecordSuccess();
            return queueUrl;
        }
        catch (Exception exception)
        {
            _availability.RecordFailure(exception);
            throw;
        }
        finally
        {
            _queueProvisioningGate.Release();
        }
    }

    private async Task<string> GetOrCreateRawQueueAsync(
        string queueName,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = await _client.GetQueueUrlAsync(
                queueName,
                cancellationToken);
            return url.QueueUrl;
        }
        catch (QueueDoesNotExistException)
        {
            var createResponse = await _client.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = queueName
                },
                cancellationToken);
            return createResponse.QueueUrl;
        }
    }

    public async Task<IQueueMessage?> ReceiveAsync(string queueUrl, int visibilityTimeoutSeconds, CancellationToken cancellationToken = default)
    {
        // ReceiveMessage uses a five-second long poll. Its timeout must remain
        // longer than that poll; the shorter publisher timeout must not cancel
        // healthy empty receives.
        using var requestCts =
            CreateRequestCancellationTokenSource(
                cancellationToken,
                TimeSpan.FromSeconds(
                    Math.Max(
                        7,
                        _requestTimeout.TotalSeconds)));
        try
        {
            var response = await _client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                VisibilityTimeout = visibilityTimeoutSeconds,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames =
                [
                    MessageSystemAttributeName.ApproximateReceiveCount,
                    MessageSystemAttributeName.SentTimestamp
                ]
            }, requestCts.Token);

            _availability.RecordSuccess();
            if (response.Messages.Count == 0) return null;

            var msg = new SqsMessage(response.Messages[0]);
            ReliantTelemetry.RecordQueueReceive(queueUrl, msg);
            return msg;
        }
        catch (Exception exception)
        {
            _availability.RecordFailure(exception);
            throw;
        }
    }

    public async Task RenewVisibilityAsync(
        string queueUrl,
        string receiptHandle,
        int visibilityTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (visibilityTimeoutSeconds is < 1 or > 43200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(visibilityTimeoutSeconds),
                "SQS visibility timeout must be between 1 and 43200 seconds.");
        }

        try
        {
            using var requestCts =
                CreateRequestCancellationTokenSource(
                    cancellationToken,
                    _requestTimeout);
            await _client.ChangeMessageVisibilityAsync(
                new ChangeMessageVisibilityRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = receiptHandle,
                    VisibilityTimeout = visibilityTimeoutSeconds
                },
                requestCts.Token);
            _availability.RecordSuccess();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _availability.RecordFailure(ex);
            throw ClassifyVisibilityRenewalFailure(ex);
        }
    }

    public async Task DeleteAsync(string queueUrl, string receiptHandle, CancellationToken cancellationToken = default)
    {
        using var requestCts =
            CreateRequestCancellationTokenSource(
                cancellationToken,
                _requestTimeout);
        try
        {
            await _client.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
                ReceiptHandle = receiptHandle
            }, requestCts.Token);
            _availability.RecordSuccess();
            ReliantTelemetry.RecordQueueDelete(queueUrl, "success");
        }
        catch (Exception exception)
        {
            _availability.RecordFailure(exception);
            ReliantTelemetry.RecordQueueDelete(queueUrl, "failure");
            throw;
        }
    }

    public async Task SendAsync(string queueUrl, string messageBody, string messageId, string messageType, CancellationToken cancellationToken = default)
        => await SendAsync(
            queueUrl,
            messageBody,
            messageId,
            messageType,
            new QueueMessageTelemetryContext(
                null,
                null,
                null,
                null,
                null),
            cancellationToken);

    public async Task SendAsync(
        string queueUrl,
        string messageBody,
        string messageId,
        string messageType,
        QueueMessageTelemetryContext telemetryContext,
        CancellationToken cancellationToken = default)
    {
        var attributes = new Dictionary<string, MessageAttributeValue>
        {
            ["MessageId"] = new MessageAttributeValue { StringValue = messageId, DataType = "String" },
            ["MessageType"] = new MessageAttributeValue { StringValue = messageType, DataType = "String" }
        };
        AddAttribute(attributes, "traceparent", telemetryContext.TraceParent);
        AddAttribute(attributes, "tracestate", telemetryContext.TraceState);
        AddAttribute(
            attributes,
            "CorrelationId",
            telemetryContext.CorrelationId);
        AddAttribute(
            attributes,
            "CausationId",
            telemetryContext.CausationId);
        AddAttribute(
            attributes,
            "DeploymentVersion",
            telemetryContext.DeploymentVersion);

        using var requestCts =
            CreateRequestCancellationTokenSource(
                cancellationToken,
                _publishTimeout);
        try
        {
            await _client.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = messageBody,
                MessageAttributes = attributes
            }, requestCts.Token);
            _availability.RecordSuccess();
        }
        catch (Exception exception)
        {
            _availability.RecordFailure(exception);
            throw;
        }
    }

    public async Task<QueueMetricsSnapshot?> GetMetricsAsync(
        string queueUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var requestCts =
                CreateRequestCancellationTokenSource(
                    cancellationToken,
                    _requestTimeout);
            var response = await _client.GetQueueAttributesAsync(
                queueUrl,
                [
                    QueueAttributeName.ApproximateNumberOfMessages,
                    QueueAttributeName.ApproximateNumberOfMessagesNotVisible,
                    QueueAttributeName.ApproximateNumberOfMessagesDelayed
                ],
                requestCts.Token);
            _availability.RecordSuccess();
            return new QueueMetricsSnapshot(
                ParseLong(response.ApproximateNumberOfMessages),
                ParseLong(response.ApproximateNumberOfMessagesNotVisible),
                ParseLong(response.ApproximateNumberOfMessagesDelayed));
        }
        catch (Exception exception)
        {
            _availability.RecordFailure(exception);
            throw;
        }
    }

    private static void AddAttribute(
        IDictionary<string, MessageAttributeValue> attributes,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            attributes[name] = new MessageAttributeValue
            {
                StringValue = value,
                DataType = "String"
            };
        }
    }

    private static long ParseLong(int value) => value;

    private static CancellationTokenSource
        CreateRequestCancellationTokenSource(
            CancellationToken callerToken,
            TimeSpan timeout)
    {
        var requestCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerToken);
        requestCts.CancelAfter(timeout);
        return requestCts;
    }

    private static QueueVisibilityRenewalException
        ClassifyVisibilityRenewalFailure(Exception exception)
    {
        if (exception is AmazonServiceException serviceException)
        {
            var errorCode = serviceException.ErrorCode ?? string.Empty;
            var errorMessage = serviceException.Message ?? string.Empty;
            if (errorCode.Contains(
                    "ReceiptHandle",
                    StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains(
                    "receipt handle",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new QueueVisibilityRenewalException(
                    QueueVisibilityFailureKind.InvalidReceiptHandle,
                    isTransient: false,
                    $"SQS rejected the receipt handle: {serviceException.Message}",
                    serviceException);
            }

            if (serviceException.StatusCode ==
                    (HttpStatusCode)429 ||
                errorCode.Contains(
                    "Throttl",
                    StringComparison.OrdinalIgnoreCase) ||
                errorCode.Equals(
                    "RequestLimitExceeded",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new QueueVisibilityRenewalException(
                    QueueVisibilityFailureKind.RateLimited,
                    isTransient: true,
                    $"SQS throttled visibility renewal: {serviceException.Message}",
                    serviceException);
            }

            var transient =
                serviceException.StatusCode ==
                    HttpStatusCode.RequestTimeout ||
                serviceException.StatusCode >=
                    HttpStatusCode.InternalServerError ||
                serviceException.StatusCode == 0;
            return new QueueVisibilityRenewalException(
                transient
                    ? QueueVisibilityFailureKind
                        .TransientServiceFailure
                    : QueueVisibilityFailureKind
                        .PermanentFailure,
                transient,
                $"SQS visibility renewal failed: {serviceException.Message}",
                serviceException);
        }

        if (exception is TimeoutException or TaskCanceledException)
        {
            return new QueueVisibilityRenewalException(
                QueueVisibilityFailureKind.Timeout,
                isTransient: true,
                $"SQS visibility renewal timed out: {exception.Message}",
                exception);
        }

        if (exception is HttpRequestException or IOException)
        {
            return new QueueVisibilityRenewalException(
                QueueVisibilityFailureKind.TransientServiceFailure,
                isTransient: true,
                $"SQS visibility renewal transport failed: {exception.Message}",
                exception);
        }

        if (exception.InnerException is not null)
        {
            return ClassifyVisibilityRenewalFailure(
                exception.InnerException);
        }

        return new QueueVisibilityRenewalException(
            QueueVisibilityFailureKind.TransientServiceFailure,
            isTransient: true,
            $"SQS visibility renewal failed: {exception.Message}",
            exception);
    }
}

internal sealed class SqsMessage(Message msg) : IQueueMessage
{
    // The Outbox Id is the stable logical MessageId used by Inbox deduplication.
    // SQS assigns a new physical MessageId to every SendMessage call, so using
    // msg.MessageId here would make a duplicate publish look like a new event.
    // Fall back to the physical id for externally-produced messages that do not
    // carry Reliant's logical MessageId attribute.
    public string MessageId =>
        msg.MessageAttributes.TryGetValue("MessageId", out var attr) &&
        !string.IsNullOrWhiteSpace(attr.StringValue)
            ? attr.StringValue
            : msg.MessageId;
    public string MessageType => msg.MessageAttributes.TryGetValue("MessageType", out var attr) ? attr.StringValue : "Unknown";
    public string Payload => msg.Body;
    public int ApproximateReceiveCount =>
        msg.Attributes.TryGetValue(MessageSystemAttributeName.ApproximateReceiveCount, out var count) &&
        int.TryParse(count, out var parsed)
            ? parsed
            : 0;
    public string ReceiptHandle => msg.ReceiptHandle;
    public string PhysicalMessageId => msg.MessageId;
    public string? CorrelationId => Attribute("CorrelationId");
    public string? CausationId => Attribute("CausationId");
    public string? TraceParent => Attribute("traceparent");
    public string? TraceState => Attribute("tracestate");
    public string? DeploymentVersion => Attribute("DeploymentVersion");
    public DateTimeOffset? SentAt =>
        msg.Attributes.TryGetValue(
            MessageSystemAttributeName.SentTimestamp,
            out var timestamp) &&
        long.TryParse(
            timestamp,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

    private string? Attribute(string name)
        => msg.MessageAttributes.TryGetValue(name, out var attribute) &&
            !string.IsNullOrWhiteSpace(attribute.StringValue)
            ? attribute.StringValue
            : null;
}
