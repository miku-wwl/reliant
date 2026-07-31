using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;

namespace Reliant.Infrastructure.Queue;

public class SqsQueueAdapter : IQueueAdapter
{
    private readonly AmazonSQSClient _client;

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
    }

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
            WaitTimeSeconds = 5
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

    public async Task SendAsync(string queueUrl, string messageBody, string messageId, CancellationToken cancellationToken = default)
    {
        var attributes = new Dictionary<string, MessageAttributeValue>
        {
            ["MessageId"] = new MessageAttributeValue { StringValue = messageId, DataType = "String" }
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
    public string MessageId => msg.MessageId;
    public string MessageType => msg.MessageAttributes.TryGetValue("MessageType", out var attr) ? attr.StringValue : "Unknown";
    public string Payload => msg.Body;
    public int ApproximateReceiveCount => 0;
    public string ReceiptHandle => msg.ReceiptHandle;
}
