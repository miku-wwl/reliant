using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Reliant.Application.Abstractions;
using Reliant.Application.Observability;
using Reliant.Domain.Enums;

namespace Reliant.Tests.Integration.Phase4;

[Trait("Category", "Phase4")]
[Trait("Category", "Unit")]
public sealed class TelemetryContractTests
{
    private static readonly HashSet<string> ForbiddenMetricTags =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "TenantId",
            "OrganizationId",
            "ContributionId",
            "MessageId",
            "JobRunId",
            "AttemptId",
            "ProviderReference",
            "IdempotencyKey",
            "ErrorMessage"
        };

    [Fact]
    public void Metrics_ShouldUseOnlyBoundedOperationalLabels()
    {
        var measurements = new ConcurrentBag<MetricMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ReliantTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
                measurements.Add(
                    new MetricMeasurement(
                        instrument.Name,
                        tags.ToArray().ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value))));
        listener.Start();

        ReliantTelemetry.RecordProviderRequest(
            "submit",
            "failure",
            TimeSpan.FromMilliseconds(25),
            ErrorCategory.RateLimited);
        ReliantTelemetry.RecordQueuePublish(
            "reliant-processing-tenant-specific-test-name",
            "success");
        ReliantTelemetry.RecordWorkerRun(
            "processing",
            "success",
            TimeSpan.FromMilliseconds(10));

        Assert.Contains(
            measurements,
            x => x.Name == "provider_request_total");
        Assert.Contains(
            measurements,
            x => x.Name == "queue_publish_total");
        Assert.All(
            measurements,
            measurement => Assert.Empty(
                measurement.Tags.Keys.Intersect(
                    ForbiddenMetricTags,
                    StringComparer.OrdinalIgnoreCase)));

        var queueMeasurement = Assert.Single(
            measurements,
            x => x.Name == "queue_publish_total");
        Assert.Equal("processing", queueMeasurement.Tags["queue"]);
    }

    [Fact]
    public void Redelivery_ShouldCreateNewConsumerSpanLinkedToOriginalTrace()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == ReliantTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var traceParent =
            $"00-{parentTraceId}-{parentSpanId}-01";
        var message = new TestQueueMessage(traceParent);

        using var consumer = ReliantTelemetry.StartQueueConsumer(
            message,
            "processing",
            "processing");

        Assert.NotNull(consumer);
        Assert.NotEqual(parentTraceId, consumer!.TraceId);
        var link = Assert.Single(consumer.Links);
        Assert.Equal(parentTraceId, link.Context.TraceId);
        Assert.Equal(parentSpanId, link.Context.SpanId);
        Assert.Equal(
            "logical-message-1",
            consumer.GetTagItem("messaging.message.id"));
    }

    [Fact]
    public void TenantSafeId_ShouldBeStableAndNotExposeTenantGuid()
    {
        var organizationId = Guid.Parse(
            "8d04a6f7-71ca-4269-a92f-74b00f139907");

        var first = ReliantTelemetry.TenantSafeId(organizationId);
        var second = ReliantTelemetry.TenantSafeId(organizationId);

        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
        Assert.DoesNotContain(
            organizationId.ToString("N"),
            first,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record MetricMeasurement(
        string Name,
        IReadOnlyDictionary<string, object?> Tags);

    private sealed class TestQueueMessage(string traceParent) :
        IQueueMessage
    {
        public string MessageId => "logical-message-1";
        public string PhysicalMessageId => "physical-message-2";
        public string MessageType => "ContributionCreated";
        public string Payload => "{}";
        public int ApproximateReceiveCount => 2;
        public string ReceiptHandle => "receipt";
        public string? TraceParent => traceParent;
        public string? CorrelationId => "correlation-1";
        public string? CausationId => "cause-1";
        public string? DeploymentVersion => "test";
    }
}
