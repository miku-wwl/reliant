using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "LocalStack")]
public class LocalStackSqsTests : IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _fixture;

    public LocalStackSqsTests(LocalStackFixture fixture)
    {
        _fixture = fixture;
    }

    private SqsQueueAdapter CreateAdapter()
    {
        var configDict = new Dictionary<string, string?>
        {
            ["Queue:Endpoint"] = _fixture.Endpoint,
            ["Queue:Region"] = "us-west-1"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        return new SqsQueueAdapter(config);
    }

    [Fact]
    public async Task SendAndReceive_ShouldRoundTripMessage()
    {
        var adapter = CreateAdapter();
        var queueUrl = await adapter.GetOrCreateQueueAsync("test-roundtrip");
        var publisher = new QueueMessagePublisher(adapter);

        await publisher.PublishAsync("test-roundtrip", "ContributionCreated", "{\"contributionId\":\"abc\"}", "msg-roundtrip-1");

        var message = await adapter.ReceiveAsync(queueUrl, 30);
        Assert.NotNull(message);
        Assert.Equal("ContributionCreated", message!.MessageType);
        Assert.Contains("abc", message.Payload);

        await adapter.DeleteAsync(queueUrl, message.ReceiptHandle);
    }

    [Fact]
    public async Task Delete_ShouldRemoveMessage()
    {
        var adapter = CreateAdapter();
        var queueUrl = await adapter.GetOrCreateQueueAsync("test-delete");
        var publisher = new QueueMessagePublisher(adapter);

        await publisher.PublishAsync("test-delete", "ContributionCreated", "payload-1", "msg-delete-1");

        var first = await adapter.ReceiveAsync(queueUrl, 30);
        Assert.NotNull(first);
        await adapter.DeleteAsync(queueUrl, first!.ReceiptHandle);

        // After delete there must be nothing left on the queue.
        var second = await adapter.ReceiveAsync(queueUrl, 1);
        Assert.Null(second);
    }

    [Fact]
    public async Task VisibilityTimeout_ShouldRedeliverUnackedMessage()
    {
        var adapter = CreateAdapter();
        var queueUrl = await adapter.GetOrCreateQueueAsync("test-visibility");
        var publisher = new QueueMessagePublisher(adapter);

        await publisher.PublishAsync("test-visibility", "ContributionCreated", "payload-vis-1", "msg-vis-1");

        // Receive with a 1 second visibility timeout but never ACK.
        var first = await adapter.ReceiveAsync(queueUrl, 1);
        Assert.NotNull(first);
        Assert.Equal(1, first!.ApproximateReceiveCount);

        // After the visibility window expires, the message is redelivered.
        await Task.Delay(TimeSpan.FromSeconds(3));

        var second = await adapter.ReceiveAsync(queueUrl, 30);
        Assert.NotNull(second);
        Assert.Equal(2, second!.ApproximateReceiveCount);

        await adapter.DeleteAsync(queueUrl, second.ReceiptHandle);
    }

    [Fact]
    public async Task UnackedMessage_ShouldNotBeVisible_WithinVisibilityWindow()
    {
        var adapter = CreateAdapter();
        var queueUrl = await adapter.GetOrCreateQueueAsync("test-invisible");
        var publisher = new QueueMessagePublisher(adapter);

        await publisher.PublishAsync("test-invisible", "ContributionCreated", "payload-invis-1", "msg-invis-1");

        var first = await adapter.ReceiveAsync(queueUrl, 30);
        Assert.NotNull(first);

        // While visible, the message must not be returned to another receiver.
        var duringWindow = await adapter.ReceiveAsync(queueUrl, 1);
        Assert.Null(duringWindow);

        await adapter.DeleteAsync(queueUrl, first!.ReceiptHandle);
    }

    [Fact]
    public async Task DuplicateDelivery_ShouldBeObservable_AndDeduplicatedByConsumer()
    {
        var adapter = CreateAdapter();
        var queueUrl = await adapter.GetOrCreateQueueAsync("test-duplicate");
        var publisher = new QueueMessagePublisher(adapter);

        // Same logical event published twice (at-least-once delivery can deliver
        // duplicates); the consumer's inbox dedup is the guard, not the queue.
        await publisher.PublishAsync("test-duplicate", "ContributionCreated", "payload-dup-1", "msg-dup-1");
        await publisher.PublishAsync("test-duplicate", "ContributionCreated", "payload-dup-1", "msg-dup-1");

        var first = await adapter.ReceiveAsync(queueUrl, 30);
        var second = await adapter.ReceiveAsync(queueUrl, 30);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Payload, second!.Payload);

        await adapter.DeleteAsync(queueUrl, first.ReceiptHandle);
        await adapter.DeleteAsync(queueUrl, second!.ReceiptHandle);
    }
}
