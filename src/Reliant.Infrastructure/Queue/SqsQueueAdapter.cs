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
    private readonly ConcurrentDictionary<string, string> _queueUrls =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queueProvisioningGate = new(1, 1);

    public SqsQueueAdapter(IConfiguration configuration)
    {
        var endpoint = configuration["Queue:Endpoint"] ?? "http://localhost:4566";
        var region = configuration["Queue:Region"] ?? "us-west-1";

        var config = new AmazonSQSConfig
        {
            ServiceURL = endpoint,
            AuthenticationRegion = region
        };

        if (endpoint.Contains("localhost") || endpoint.Contains("4566"))
        {
            config.ServiceURL = endpoint;
        }

        _client = new AmazonSQSClient("test", "test", config);
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

        await _queueProvisioningGate.WaitAsync(cancellationToken);
        try
        {
            if (_queueUrls.TryGetValue(queueName, out cachedQueueUrl))
            {
                return cachedQueueUrl;
            }

            var deadLetterQueueUrl = await GetOrCreateRawQueueAsync(
                $"{queueName}-dlq",
                cancellationToken);
            var deadLetterAttributes =
                await _client.GetQueueAttributesAsync(
                    deadLetterQueueUrl,
                    [QueueAttributeName.QueueArn],
                    cancellationToken);
            if (string.IsNullOrWhiteSpace(deadLetterAttributes.QueueARN))
            {
                throw new InvalidOperationException(
                    $"SQS did not return an ARN for DLQ {queueName}-dlq");
            }

            var queueUrl = await GetOrCreateRawQueueAsync(
                queueName,
                cancellationToken);
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
                cancellationToken);

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
        }, cancellationToken);

        if (response.Messages.Count == 0) return null;

        var msg = response.Messages[0];
        return new SqsMessage(msg);
    }

    public async Task DeleteAsync(string queueUrl, string receiptHandle, CancellationToken cancellationToken = default)
    {
        await _client.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = receiptHandle
        }, cancellationToken);
    }

    public async Task SendAsync(string queueUrl, string messageBody, string messageId, string messageType, CancellationToken cancellationToken = default)
    {
        var attributes = new Dictionary<string, MessageAttributeValue>
        {
            ["MessageId"] = new MessageAttributeValue { StringValue = messageId, DataType = "String" },
            ["MessageType"] = new MessageAttributeValue { StringValue = messageType, DataType = "String" }
        };

        await _client.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody,
            MessageAttributes = attributes
        }, cancellationToken);
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
