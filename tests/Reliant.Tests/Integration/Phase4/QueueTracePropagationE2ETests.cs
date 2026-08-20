using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using Reliant.Application.Observability;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;

namespace Reliant.Tests.Integration.Phase4;

[Trait("Category", "Phase4")]
[Trait("Category", "Integration")]
[Trait("Dependency", "LocalStack")]
public sealed class QueueTracePropagationE2ETests(
    LocalStackFixture fixture) : IClassFixture<LocalStackFixture>
{
    [Fact]
    public async Task Sqs_ShouldRoundTripTraceCorrelationAndDeploymentContext()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == ReliantTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Queue:Endpoint"] = fixture.Endpoint,
                    ["Queue:Region"] = "us-west-1"
                })
            .Build();
        var adapter = new SqsQueueAdapter(configuration);
        var publisher = new QueueMessagePublisher(adapter);
        var queueName = $"phase4-trace-{Guid.NewGuid():N}";
        var queueUrl = await adapter.GetOrCreateQueueAsync(queueName);

        using var root = ReliantTelemetry.StartActivity(
            "phase4 trace propagation test");
        Assert.NotNull(root);
        var rootTraceId = root!.TraceId;
        await publisher.PublishAsync(
            queueName,
            "ContributionCreated",
            "{}",
            "logical-message-1",
            new QueueMessageTelemetryContext(
                "correlation-1",
                "cause-1",
                Activity.Current?.Id,
                Activity.Current?.TraceStateString,
                "phase4-test"));

        var message = await adapter.ReceiveAsync(queueUrl, 30);
        Assert.NotNull(message);
        Assert.Equal("logical-message-1", message!.MessageId);
        Assert.NotEqual(message.MessageId, message.PhysicalMessageId);
        Assert.Equal("correlation-1", message.CorrelationId);
        Assert.Equal("cause-1", message.CausationId);
        Assert.Equal("phase4-test", message.DeploymentVersion);
        Assert.False(string.IsNullOrWhiteSpace(message.TraceParent));

        using var consumer = ReliantTelemetry.StartQueueConsumer(
            message,
            "other",
            "processing");
        Assert.NotNull(consumer);
        Assert.Equal(rootTraceId, consumer!.TraceId);

        await adapter.DeleteAsync(queueUrl, message.ReceiptHandle);
    }
}
