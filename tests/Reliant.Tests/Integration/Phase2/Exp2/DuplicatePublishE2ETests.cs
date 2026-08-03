using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp2;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public class DuplicatePublishE2ETests(ITestOutputHelper output)
{
    private sealed class RecordingQueueAdapter(IQueueAdapter inner) : IQueueAdapter
    {
        private readonly ConcurrentQueue<string> _sentMessageIds = new();
        private readonly ConcurrentQueue<string> _receivedMessageIds = new();
        private int _deleteCount;

        public IReadOnlyCollection<string> SentMessageIds => _sentMessageIds.ToArray();
        public IReadOnlyCollection<string> ReceivedMessageIds => _receivedMessageIds.ToArray();
        public int SendCount => _sentMessageIds.Count;
        public int ReceiveCount => _receivedMessageIds.Count;
        public int DeleteCount => _deleteCount;

        public Task<string> GetOrCreateQueueAsync(
            string queueName,
            CancellationToken cancellationToken = default)
            => inner.GetOrCreateQueueAsync(queueName, cancellationToken);

        public async Task<IQueueMessage?> ReceiveAsync(
            string queueUrl,
            int visibilityTimeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            var message = await inner.ReceiveAsync(
                queueUrl,
                visibilityTimeoutSeconds,
                cancellationToken);

            if (message is not null)
            {
                _receivedMessageIds.Enqueue(message.MessageId);
            }

            return message;
        }

        public async Task DeleteAsync(
            string queueUrl,
            string receiptHandle,
            CancellationToken cancellationToken = default)
        {
            await inner.DeleteAsync(queueUrl, receiptHandle, cancellationToken);
            Interlocked.Increment(ref _deleteCount);
        }

        public async Task SendAsync(
            string queueUrl,
            string messageBody,
            string messageId,
            string messageType,
            CancellationToken cancellationToken = default)
        {
            await inner.SendAsync(
                queueUrl,
                messageBody,
                messageId,
                messageType,
                cancellationToken);
            _sentMessageIds.Enqueue(messageId);
        }
    }

    private static ReliantDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ReliantDbContext(options);
    }

    private static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(300);
        }

        return await condition();
    }

    [Fact]
    public async Task SameOutboxPublishedTwice_ShouldBeReceivedTwice_ButProduceOneBusinessEffect()
    {
        var startedAt = DateTime.UtcNow;
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();

        var queueConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:Endpoint"] = fixture.SqsEndpoint,
                ["Queue:Region"] = "us-west-1"
            })
            .Build();
        var queueProbe = new RecordingQueueAdapter(
            new SqsQueueAdapter(queueConfiguration));

        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeProcessing: true,
            includeReconciliation: false,
            visibilityTimeoutSeconds: 3,
            queueAdapterOverride: queueProbe);

        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var outboxMessageId = Guid.NewGuid();
        var correlationId = "phase2-exp2-duplicate-publish";
        var payload = JsonSerializer.Serialize(new ContributionProcessingMessage(
            Version: 1,
            ContributionId: contributionId,
            OrganizationId: organizationId,
            Trigger: "Created",
            CorrelationId: correlationId));

        // The Outbox row is marked Sent in the fixture so the hosted publisher
        // does not race the two explicit PublishAsync calls below. The experiment
        // controls publication directly while retaining the real Outbox identity,
        // SQS adapter, consumer, database and provider path.
        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            db.Organizations.Add(new Organization
            {
                Id = organizationId,
                Name = "Phase 2 Duplicate Publish Lab",
                Status = OrganizationStatus.Active,
                Version = 0
            });
            db.Campaigns.Add(new Campaign
            {
                Id = campaignId,
                OrganizationId = organizationId,
                Name = "Experiment 2",
                Status = CampaignStatus.Active,
                Version = 0
            });
            db.Contributions.Add(new Contribution
            {
                Id = contributionId,
                OrganizationId = organizationId,
                CampaignId = campaignId,
                ExternalReference = "PHASE2-EXP2-001",
                Amount = 100m,
                Currency = "NZD",
                State = ContributionState.Created,
                Version = 0
            });
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = outboxMessageId,
                OrganizationId = organizationId,
                MessageType = "ContributionCreated",
                Payload = payload,
                CorrelationId = correlationId,
                OccurredAt = DateTime.UtcNow,
                SentAt = DateTime.UtcNow,
                Status = OutboxStatus.Sent,
                Version = 0
            });
            await db.SaveChangesAsync();
        }

        IQueueMessagePublisher publisher;
        using (var scope = fixture.Host.Services.CreateScope())
        {
            publisher = scope.ServiceProvider
                .GetRequiredService<IQueueMessagePublisher>();
        }

        var logicalMessageId = outboxMessageId.ToString();

        // Publish #1: run the full consumer/provider path to completion.
        await publisher.PublishAsync(
            fixture.QueueName,
            "ContributionCreated",
            payload,
            logicalMessageId);

        var firstCompleted = await WaitUntilAsync(async () =>
        {
            await using var db = CreateDbContext(fixture.PgConnectionString);
            var state = await db.Contributions.IgnoreQueryFilters()
                .Where(c => c.Id == contributionId)
                .Select(c => c.State)
                .SingleAsync();
            var inboxCount = await db.InboxMessages.IgnoreQueryFilters()
                .CountAsync(m => m.MessageId == logicalMessageId);

            return state == ContributionState.Succeeded
                && inboxCount == 1
                && queueProbe.ReceiveCount >= 1
                && queueProbe.DeleteCount >= 1;
        }, TimeSpan.FromSeconds(60));

        Assert.True(
            firstCompleted,
            "First publish did not complete.\n" + fixture.RecentLogs(40));

        var provider = fixture.Host.Services.GetRequiredService<IProvider>()
            as SandboxProvider;
        Assert.NotNull(provider);
        Assert.Equal(1, provider!.OperationCount);

        int transitionsAfterFirst;
        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            transitionsAfterFirst = await db.StateTransitions.IgnoreQueryFilters()
                .CountAsync(t => t.ContributionId == contributionId);
        }

        output.WriteLine(
            "AFTER PUBLISH #1 | OutboxId={0} | QueueSend=1 | " +
            "QueueReceive={1} | QueueDelete={2} | InboxRows=1 | " +
            "BusinessState=Succeeded | ProviderOperations=1",
            outboxMessageId,
            queueProbe.ReceiveCount,
            queueProbe.DeleteCount);

        // Publish #2: exact same Outbox identity, type and payload.
        await publisher.PublishAsync(
            fixture.QueueName,
            "ContributionCreated",
            payload,
            logicalMessageId);

        var duplicateDeduped = await WaitUntilAsync(() =>
        {
            var dedupLogSeen = fixture.LogLines.Any(line =>
                line.Contains(
                    "already processed (inbox dedup)",
                    StringComparison.Ordinal));
            var consumerEntries = fixture.LogLines.Count(line =>
                line.Contains("Processing message", StringComparison.Ordinal));

            return Task.FromResult(
                queueProbe.SendCount == 2
                && queueProbe.ReceiveCount >= 2
                && queueProbe.DeleteCount >= 2
                && consumerEntries >= 2
                && dedupLogSeen);
        }, TimeSpan.FromSeconds(60));

        Assert.True(
            duplicateDeduped,
            "Duplicate publish was not received and deduplicated.\n" +
            $"Send={queueProbe.SendCount}, " +
            $"Receive={queueProbe.ReceiveCount}, " +
            $"Delete={queueProbe.DeleteCount}\n" +
            fixture.RecentLogs(50));

        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var contributions = await db.Contributions.IgnoreQueryFilters()
                .Where(c => c.Id == contributionId)
                .ToListAsync();
            var outboxRows = await db.OutboxMessages.IgnoreQueryFilters()
                .Where(m => m.Id == outboxMessageId)
                .ToListAsync();
            var inboxRows = await db.InboxMessages.IgnoreQueryFilters()
                .Where(m => m.MessageId == logicalMessageId)
                .ToListAsync();
            var attempts = await db.ProcessingAttempts.IgnoreQueryFilters()
                .Where(a => a.ContributionId == contributionId)
                .ToListAsync();
            var references = await db.ProviderReferences.IgnoreQueryFilters()
                .Where(r => r.ContributionId == contributionId)
                .ToListAsync();
            var transitionsAfterDuplicate =
                await db.StateTransitions.IgnoreQueryFilters()
                    .CountAsync(t => t.ContributionId == contributionId);
            var deadLetters = await db.DeadLetterRecords.IgnoreQueryFilters()
                .CountAsync();

            Assert.Single(contributions);
            Assert.Equal(
                ContributionState.Succeeded,
                contributions[0].State);
            Assert.Single(outboxRows);
            Assert.Single(inboxRows);
            Assert.Equal(logicalMessageId, inboxRows[0].MessageId);
            Assert.Single(attempts);
            Assert.Equal(AttemptStatus.Succeeded, attempts[0].Status);
            Assert.Single(references);
            Assert.Equal(transitionsAfterFirst, transitionsAfterDuplicate);
            Assert.Equal(0, deadLetters);
        }

        Assert.Equal(2, queueProbe.SendCount);
        Assert.True(queueProbe.ReceiveCount >= 2);
        Assert.All(
            queueProbe.SentMessageIds,
            id => Assert.Equal(logicalMessageId, id));
        Assert.All(
            queueProbe.ReceivedMessageIds,
            id => Assert.Equal(logicalMessageId, id));
        Assert.Equal(1, provider.OperationCount);

        output.WriteLine(
            "AFTER PUBLISH #2 | OutboxId={0} | QueueSend={1} | " +
            "QueueReceive={2} | QueueDelete={3} | ConsumerEntries>=2 | " +
            "InboxRows=1 | ProcessingAttempts=1 | ProviderReferences=1 | " +
            "ProviderOperations=1 | DuplicateStateTransitions=0 | " +
            "DeadLetters=0",
            outboxMessageId,
            queueProbe.SendCount,
            queueProbe.ReceiveCount,
            queueProbe.DeleteCount);
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O}",
            startedAt,
            DateTime.UtcNow);
    }
}
