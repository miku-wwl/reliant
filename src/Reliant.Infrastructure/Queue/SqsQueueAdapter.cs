using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using System.Collections.Concurrent;
using System.Globalization;
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

    public SqsQueueAdapter(IConfiguration configuration)
    {
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
            return queueUrl;
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
        var response = await _client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            VisibilityTimeout = visibilityTimeoutSeconds,
            WaitTimeSeconds = 5,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames =
            [
                MessageSystemAttributeName.ApproximateReceiveCount
            ]
        }, requestCts.Token);

        if (response.Messages.Count == 0) return null;

        var msg = response.Messages[0];
        return new SqsMessage(msg);
    }

    public async Task DeleteAsync(string queueUrl, string receiptHandle, CancellationToken cancellationToken = default)
    {
        using var requestCts =
            CreateRequestCancellationTokenSource(
                cancellationToken,
                _requestTimeout);
        await _client.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = receiptHandle
        }, requestCts.Token);
    }

    public async Task SendAsync(string queueUrl, string messageBody, string messageId, string messageType, CancellationToken cancellationToken = default)
    {
        var attributes = new Dictionary<string, MessageAttributeValue>
        {
            ["MessageId"] = new MessageAttributeValue { StringValue = messageId, DataType = "String" },
            ["MessageType"] = new MessageAttributeValue { StringValue = messageType, DataType = "String" }
        };

        using var requestCts =
            CreateRequestCancellationTokenSource(
                cancellationToken,
                _publishTimeout);
        await _client.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody,
            MessageAttributes = attributes
        }, requestCts.Token);
    }

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
}
