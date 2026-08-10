using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Application.Messaging;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp14;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class ProviderBacklogAndRecoveryE2ETests(
    ITestOutputHelper output)
{
    private const int MessageCount = 50;
    private const int FailureThreshold = 5;
    private const int OpenDurationSeconds = 3;
    private const int ProcessingConcurrency = 1;
    private const int ProviderDelayMs = 100;

    private sealed record WorkItem(
        Guid ContributionId,
        Guid MessageId,
        string Payload);

    private sealed record QueueDepth(int Visible, int InFlight)
    {
        public int Total => Visible + InFlight;
    }

    private sealed record Snapshot(
        int Succeeded,
        int RetryPending,
        int ProcessingAttempts,
        int FailedProcessingAttempts,
        int SucceededProcessingAttempts,
        int ProviderReferences,
        int DeadLetters,
        int ActiveLeases,
        int DuplicateProviderReferences);

    [Fact]
    public async Task ProviderOutage_ShouldBoundCalls_ExposeBacklog_AndDrainAfterRecovery()
    {
        var startedAt = DateTime.UtcNow;
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();

        var queueConfiguration = CreateQueueConfiguration(fixture);
        var queue = new SqsQueueAdapter(queueConfiguration);
        using var sqs = CreateSqsClient(fixture.SqsEndpoint);
        var queueUrl = await queue.GetOrCreateQueueAsync(
            fixture.QueueName);
        var items = await SeedWorkAsync(
            fixture.PgConnectionString);

        await Task.WhenAll(items.Select(item =>
            queue.SendAsync(
                queueUrl,
                item.Payload,
                item.MessageId.ToString(),
                "ContributionCreated")));

        Assert.True(
            await WaitUntilAsync(
                async () =>
                    (await GetQueueDepthAsync(sqs, queueUrl)).Total ==
                    MessageCount,
                TimeSpan.FromSeconds(15)),
            "The initial 50-message backlog was not observable.");

        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var oldestSentAt = await SampleOldestSentAtAsync(
            sqs,
            queueUrl,
            MessageCount);
        Assert.True(
            await WaitUntilAsync(
                async () =>
                    (await GetQueueDepthAsync(sqs, queueUrl)).Total ==
                    MessageCount,
                TimeSpan.FromSeconds(10)),
            "The SentTimestamp sample did not restore all messages.");

        var circuit = new CircuitBreaker(
            FailureThreshold,
            OpenDurationSeconds);
        await fixture.StartWorkersAsync(
            providerMode: "Error5xxBeforeProcessing",
            includeReconciliation: false,
            visibilityTimeoutSeconds: 2,
            maxReceiveCount: 100,
            leaseSeconds: 10,
            heartbeatIntervalMs: 500,
            processingConcurrency: ProcessingConcurrency,
            providerSubmitDelayMs: ProviderDelayMs,
            circuitBreakerOverride: circuit);

        var provider = fixture.Host.Services
            .GetRequiredService<ISandboxProviderControl>();

        Assert.True(
            await WaitUntilAsync(
                () => Task.FromResult(
                    circuit.State == CircuitBreakerState.Open),
                TimeSpan.FromSeconds(20)),
            "Five provider 5xx responses did not open the circuit." +
            Environment.NewLine + fixture.RecentLogs(100));

        var openSnapshot = await ReadSnapshotAsync(
            fixture.PgConnectionString);
        var openDepth = await GetQueueDepthAsync(sqs, queueUrl);
        var oldestAge = DateTimeOffset.UtcNow - oldestSentAt;

        Assert.Equal(FailureThreshold, openSnapshot.ProcessingAttempts);
        Assert.Equal(FailureThreshold, openSnapshot.FailedProcessingAttempts);
        Assert.Equal(0, openSnapshot.SucceededProcessingAttempts);
        Assert.Equal(FailureThreshold, openSnapshot.RetryPending);
        Assert.Equal(0, provider.OperationCount);
        Assert.True(
            openDepth.Total >= MessageCount - FailureThreshold,
            $"Circuit did not retain enough backlog: {openDepth.Total}.");
        Assert.True(
            oldestAge >= TimeSpan.FromSeconds(1),
            $"Oldest age was only {oldestAge.TotalMilliseconds:F0}ms.");

        output.WriteLine(
            "OPEN | Threshold={0} | Provider5xxCalls={1} | " +
            "ProviderEffects={2} | RetryPending={3} | " +
            "QueueDepth={4} | OldestAgeMs={5:F0}",
            FailureThreshold,
            openSnapshot.FailedProcessingAttempts,
            provider.OperationCount,
            openSnapshot.RetryPending,
            openDepth.Total,
            oldestAge.TotalMilliseconds);

        provider.SetMode("Success");
        var recoveryStopwatch = Stopwatch.StartNew();

        var sawHalfOpen = await WaitUntilAsync(
            () => Task.FromResult(
                circuit.State == CircuitBreakerState.HalfOpen),
            TimeSpan.FromSeconds(OpenDurationSeconds + 5),
            TimeSpan.FromMilliseconds(10));
        Assert.True(
            sawHalfOpen,
            "The circuit never exposed a Half-Open probe window.");

        var operationSamples = new List<int>();
        while (
            recoveryStopwatch.Elapsed < TimeSpan.FromSeconds(10) &&
            provider.OperationCount < 10)
        {
            operationSamples.Add(provider.OperationCount);
            await Task.Delay(100);
        }
        operationSamples.Add(provider.OperationCount);

        var maxOperationsPer100Ms = operationSamples
            .Zip(operationSamples.Skip(1),
                (before, after) => after - before)
            .DefaultIfEmpty(0)
            .Max();
        Assert.True(
            maxOperationsPer100Ms <= 2,
            $"Recovery burst exceeded the concurrency guard: " +
            $"{maxOperationsPer100Ms} operations/100ms.");

        Assert.True(
            await WaitUntilAsync(
                async () =>
                {
                    var snapshot = await ReadSnapshotAsync(
                        fixture.PgConnectionString);
                    var depth = await GetQueueDepthAsync(sqs, queueUrl);
                    return snapshot.Succeeded == MessageCount &&
                        depth.Total == 0;
                },
                TimeSpan.FromSeconds(90)),
            "The provider backlog did not drain after recovery." +
            Environment.NewLine + fixture.RecentLogs(150));
        recoveryStopwatch.Stop();

        var final = await ReadSnapshotAsync(
            fixture.PgConnectionString);
        var finalDepth = await GetQueueDepthAsync(sqs, queueUrl);

        Assert.Equal(CircuitBreakerState.Closed, circuit.State);
        Assert.Equal(MessageCount, final.Succeeded);
        Assert.Equal(0, final.RetryPending);
        Assert.Equal(MessageCount + FailureThreshold,
            final.ProcessingAttempts);
        Assert.Equal(FailureThreshold,
            final.FailedProcessingAttempts);
        Assert.Equal(MessageCount,
            final.SucceededProcessingAttempts);
        Assert.Equal(MessageCount, final.ProviderReferences);
        Assert.Equal(MessageCount, provider.OperationCount);
        Assert.Equal(0, final.DeadLetters);
        Assert.Equal(0, final.ActiveLeases);
        Assert.Equal(0, final.DuplicateProviderReferences);
        Assert.Equal(0, finalDepth.Total);

        output.WriteLine(
            "HALF_OPEN | ProbeObserved={0} | " +
            "MaxProviderEffectsPer100Ms={1} | Concurrency={2}",
            sawHalfOpen,
            maxOperationsPer100Ms,
            ProcessingConcurrency);
        output.WriteLine(
            "RECOVERY | DrainMs={0} | Circuit=Closed | " +
            "QueueDepth={1}",
            recoveryStopwatch.ElapsedMilliseconds,
            finalDepth.Total);
        output.WriteLine(
            "FINAL | Succeeded={0} | Attempts={1} " +
            "({2} failed + {3} succeeded) | References={4} | " +
            "ProviderEffects={5} | DuplicateEffects={6} | " +
            "DeadLetters={7}",
            final.Succeeded,
            final.ProcessingAttempts,
            final.FailedProcessingAttempts,
            final.SucceededProcessingAttempts,
            final.ProviderReferences,
            provider.OperationCount,
            final.DuplicateProviderReferences,
            final.DeadLetters);
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O}",
            startedAt,
            DateTime.UtcNow);
    }

    private static IConfiguration CreateQueueConfiguration(
        WorkerHostFixture fixture)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Queue:Endpoint"] = fixture.SqsEndpoint,
                    ["Queue:Region"] = "us-west-1",
                    ["Queue:QueueName"] = fixture.QueueName,
                    ["Queue:MaxReceiveCount"] = "100",
                    ["Queue:RequestTimeoutSeconds"] = "5",
                    ["Queue:PublishTimeoutSeconds"] = "5",
                    ["Queue:MaxErrorRetry"] = "1"
                })
            .Build();

    private static AmazonSQSClient CreateSqsClient(string endpoint)
        => new(
            "test",
            "test",
            new AmazonSQSConfig
            {
                ServiceURL = endpoint,
                AuthenticationRegion = "us-west-1",
                MaxErrorRetry = 1
            });

    private static async Task<List<WorkItem>> SeedWorkAsync(
        string connectionString)
    {
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var items = Enumerable.Range(1, MessageCount)
            .Select(index =>
            {
                var contributionId = Guid.NewGuid();
                var messageId = Guid.NewGuid();
                return new WorkItem(
                    contributionId,
                    messageId,
                    JsonSerializer.Serialize(
                        new ContributionProcessingMessage(
                            Version: 1,
                            ContributionId: contributionId,
                            OrganizationId: organizationId,
                            Trigger: "Created",
                            CorrelationId:
                                $"phase3-exp14-{index:000}")));
            })
            .ToList();

        await using var db = CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 3 Provider Backlog Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 14",
            Status = CampaignStatus.Active,
            Version = 0
        });

        foreach (var (item, index) in items.Select(
            (item, index) => (item, index)))
        {
            db.Contributions.Add(new Contribution
            {
                Id = item.ContributionId,
                OrganizationId = organizationId,
                CampaignId = campaignId,
                ExternalReference =
                    $"PHASE3-EXP14-{index + 1:000}",
                Amount = 100m + index,
                Currency = "NZD",
                State = ContributionState.Created,
                Version = 0
            });
            var outbox = new OutboxMessage
            {
                Id = item.MessageId,
                OrganizationId = organizationId,
                MessageType = "ContributionCreated",
                Payload = item.Payload,
                CorrelationId = $"phase3-exp14-{index + 1:000}",
                OccurredAt = now.AddMilliseconds(index),
                SentAt = now,
                SendCount = 1,
                Status = OutboxStatus.Sent,
                Version = 0
            };
            db.OutboxMessages.Add(outbox);
            db.JobRuns.Add(JobRun.ForContributionProcessing(outbox));
        }

        await db.SaveChangesAsync();
        return items;
    }

    private static async Task<DateTimeOffset> SampleOldestSentAtAsync(
        AmazonSQSClient sqs,
        string queueUrl,
        int expectedCount)
    {
        var sampled = new List<Message>();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (sampled.Count < expectedCount &&
                   DateTime.UtcNow < deadline)
            {
                var response = await sqs.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 10,
                        VisibilityTimeout = 30,
                        WaitTimeSeconds = 1,
                        MessageSystemAttributeNames =
                        [MessageSystemAttributeName.SentTimestamp]
                    });
                sampled.AddRange(response.Messages);
            }

            Assert.Equal(expectedCount, sampled.Count);
            return sampled.Select(message =>
                DateTimeOffset.FromUnixTimeMilliseconds(
                    long.Parse(
                        message.Attributes[
                            MessageSystemAttributeName.SentTimestamp],
                        CultureInfo.InvariantCulture)))
                .Min();
        }
        finally
        {
            foreach (var batch in sampled.Chunk(10))
            {
                var response = await sqs
                    .ChangeMessageVisibilityBatchAsync(
                        new ChangeMessageVisibilityBatchRequest
                        {
                            QueueUrl = queueUrl,
                            Entries = batch.Select((message, index) =>
                                new ChangeMessageVisibilityBatchRequestEntry
                                {
                                    Id = $"restore-{index}",
                                    ReceiptHandle = message.ReceiptHandle,
                                    VisibilityTimeout = 0
                                }).ToList()
                        });
                Assert.Empty(response.Failed);
            }
        }
    }

    private static async Task<QueueDepth> GetQueueDepthAsync(
        AmazonSQSClient sqs,
        string queueUrl)
    {
        var response = await sqs.GetQueueAttributesAsync(
            queueUrl,
            [
                QueueAttributeName.ApproximateNumberOfMessages,
                QueueAttributeName.ApproximateNumberOfMessagesNotVisible
            ]);
        return new QueueDepth(
            response.ApproximateNumberOfMessages,
            response.ApproximateNumberOfMessagesNotVisible);
    }

    private static async Task<Snapshot> ReadSnapshotAsync(
        string connectionString)
    {
        await using var db = CreateDbContext(connectionString);
        return new Snapshot(
            Succeeded: await db.Contributions
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.State == ContributionState.Succeeded),
            RetryPending: await db.Contributions
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.State == ContributionState.RetryPending),
            ProcessingAttempts: await db.ProcessingAttempts
                .IgnoreQueryFilters()
                .CountAsync(),
            FailedProcessingAttempts: await db.ProcessingAttempts
                .IgnoreQueryFilters()
                .CountAsync(x => x.Status == AttemptStatus.Failed),
            SucceededProcessingAttempts: await db.ProcessingAttempts
                .IgnoreQueryFilters()
                .CountAsync(x => x.Status == AttemptStatus.Succeeded),
            ProviderReferences: await db.ProviderReferences
                .IgnoreQueryFilters()
                .CountAsync(),
            DeadLetters: await db.DeadLetterRecords
                .IgnoreQueryFilters()
                .CountAsync(),
            ActiveLeases: await db.Leases
                .IgnoreQueryFilters()
                .CountAsync(x => x.IsActive),
            DuplicateProviderReferences: await db.ProviderReferences
                .IgnoreQueryFilters()
                .GroupBy(x => x.ContributionId)
                .CountAsync(group => group.Count() > 1));
    }

    private static ReliantDbContext CreateDbContext(
        string connectionString)
        => new(
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(connectionString)
                .Options);

    private static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(250));
        }

        return await condition();
    }
}
