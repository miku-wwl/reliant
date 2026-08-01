using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;

namespace Reliant.Tests.TestHelpers;

/// <summary>
/// A test queue adapter built directly on the AWS SDK that, in addition to
/// counting receives/deletes, records the SQS-native ApproximateReceiveCount of
/// every message the worker receives. Because the worker itself uses this
/// adapter, the ApproximateReceiveCount is observed without racing a second
/// consumer against the worker's long-polling receive loop.
/// </summary>
public sealed class RawSqsQueueAdapter : IQueueAdapter
{
    private readonly AmazonSQSClient _client;
    private int _receiveCount;
    private int _deleteCount;
    private int _maxApproximateReceiveCount;

    public RawSqsQueueAdapter(IConfiguration configuration)
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
    }

    public int ReceiveCount => _receiveCount;
    public int DeleteCount => _deleteCount;
    public int MaxApproximateReceiveCount => _maxApproximateReceiveCount;

    public async Task<string> GetOrCreateQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = await _client.GetQueueUrlAsync(queueName, cancellationToken);
            return url.QueueUrl;
        }
        catch (QueueDoesNotExistException)
        {
            var createResponse = await _client.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName
            }, cancellationToken);
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
            AttributeNames = [MessageSystemAttributeName.ApproximateReceiveCount]
        }, cancellationToken);

        if (response.Messages.Count == 0) return null;

        var msg = response.Messages[0];
        Interlocked.Increment(ref _receiveCount);
        if (msg.Attributes.TryGetValue(MessageSystemAttributeName.ApproximateReceiveCount, out var v) &&
            int.TryParse(v, out var n))
        {
            int current;
            while (n > (current = _maxApproximateReceiveCount))
            {
                Interlocked.CompareExchange(ref _maxApproximateReceiveCount, n, current);
            }
        }
        return new TestSqsMessage(msg);
    }

    public Task DeleteAsync(string queueUrl, string receiptHandle, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _deleteCount);
        return _client.DeleteMessageAsync(new DeleteMessageRequest
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

    private sealed class TestSqsMessage(Message msg) : IQueueMessage
    {
        public string MessageId => msg.MessageId;
        public string MessageType => msg.MessageAttributes.TryGetValue("MessageType", out var attr) ? attr.StringValue : "Unknown";
        public string Payload => msg.Body;
        public int ApproximateReceiveCount =>
            msg.Attributes.TryGetValue(MessageSystemAttributeName.ApproximateReceiveCount, out var v) && int.TryParse(v, out var n) ? n : 0;
        public string ReceiptHandle => msg.ReceiptHandle;
    }
}
