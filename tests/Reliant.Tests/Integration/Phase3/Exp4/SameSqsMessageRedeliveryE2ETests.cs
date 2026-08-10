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
using Reliant.Tests.TestHelpers;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp4;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class SameSqsMessageRedeliveryE2ETests(
    ITestOutputHelper output)
{
    private sealed class RedeliveryEvidenceQueueAdapter(
        IQueueAdapter inner) : IQueueAdapter
    {
        private readonly ConcurrentQueue<string>
            _receivedMessageIds = new();
        private int _receiveCount;
        private int _deleteCount;
        private int _sendCount;
        private int _maxApproximateReceiveCount;

        public int ReceiveCount => _receiveCount;
        public int DeleteCount => _deleteCount;
        public int SendCount => _sendCount;
        public int MaxApproximateReceiveCount =>
            _maxApproximateReceiveCount;
        public IReadOnlyCollection<string> ReceivedMessageIds =>
            _receivedMessageIds.ToArray();

        public Task<string> GetOrCreateQueueAsync(
            string queueName,
            CancellationToken cancellationToken = default)
            => inner.GetOrCreateQueueAsync(
                queueName,
                cancellationToken);

        public async Task<IQueueMessage?> ReceiveAsync(
            string queueUrl,
            int visibilityTimeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            var message = await inner.ReceiveAsync(
                queueUrl,
                visibilityTimeoutSeconds,
                cancellationToken);
            if (message is null)
            {
                return null;
            }

            _receivedMessageIds.Enqueue(message.MessageId);
            Interlocked.Increment(ref _receiveCount);
            var receiveCount = message.ApproximateReceiveCount;
            int current;
            while (receiveCount >
                (current = _maxApproximateReceiveCount))
            {
                Interlocked.CompareExchange(
                    ref _maxApproximateReceiveCount,
                    receiveCount,
                    current);
            }

            return message;
        }

        public async Task DeleteAsync(
            string queueUrl,
            string receiptHandle,
            CancellationToken cancellationToken = default)
        {
            await inner.DeleteAsync(
                queueUrl,
                receiptHandle,
                cancellationToken);
            Interlocked.Increment(ref _deleteCount);
        }

        public Task RenewVisibilityAsync(
            string queueUrl,
            string receiptHandle,
            int visibilityTimeoutSeconds,
            CancellationToken cancellationToken = default)
            => inner.RenewVisibilityAsync(
                queueUrl,
                receiptHandle,
                visibilityTimeoutSeconds,
                cancellationToken);

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
            Interlocked.Increment(ref _sendCount);
        }
    }

    private static ReliantDbContext CreateDbContext(string pgConnectionString)
    {
        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(pgConnectionString)
            .Options;
        return new ReliantDbContext(options);
    }

    private static async Task<(
        Guid orgId,
        Guid contributionId,
        Guid outboxId)> SeedCreatedContributionWithOutboxAsync(
            ReliantDbContext db)
    {
        var orgId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();

        db.Set<Organization>().Add(new Organization
        {
            Id = orgId,
            Name = "Crash Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Set<Campaign>().Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = orgId,
            Name = "Crash",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Set<Contribution>().Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = orgId,
            CampaignId = campaignId,
            ExternalReference = "CRASH-001",
            Amount = 100m,
            Currency = "USD",
            State = ContributionState.Created,
            Version = 0
        });
        var outbox = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            MessageType = "ContributionCreated",
            Payload = JsonSerializer.Serialize(new ContributionProcessingMessage(
                Version: 1,
                ContributionId: contributionId,
                OrganizationId: orgId,
                Trigger: "Created",
                CorrelationId: Guid.NewGuid().ToString())),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = DateTime.UtcNow,
            Status = OutboxStatus.Pending,
            Version = 0
        };
        db.Set<OutboxMessage>().Add(outbox);
        db.Set<JobRun>().Add(
            JobRun.ForContributionProcessing(outbox));

        await db.SaveChangesAsync();
        return (orgId, contributionId, outbox.Id);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(500);
        }
        return await condition();
    }

    private static async Task WaitForQueueReadyAsync(WorkerHostFixture fixture, TimeSpan timeout)
    {
        using var scope = fixture.Host.Services.CreateScope();
        var queueAdapter = scope.ServiceProvider.GetRequiredService<IQueueAdapter>();
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await queueAdapter.GetOrCreateQueueAsync(fixture.QueueName);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        throw new TimeoutException($"Worker queue not reachable within {timeout}. Last error: {last?.Message}");
    }

    [Fact]
    public async Task CrashBeforeMessageAck_ShouldRedeliverAndDeduplicate_WithoutSecondProviderEffect()
    {
        var startedAt = DateTime.UtcNow;
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();

        // A counting adapter that observes the worker's real SQS operations.
        var innerConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:Endpoint"] = fixture.SqsEndpoint,
                ["Queue:Region"] = "us-west-1"
            })
            .Build();
        var counter = new RedeliveryEvidenceQueueAdapter(
            new SqsQueueAdapter(innerConfig));

        await fixture.StartWorkersAsync(
            providerMode: "Success",
            faultInjector: new ThrowingFaultInjector(WorkerFaultPoint.BeforeMessageAck),
            includeReconciliation: false,
            visibilityTimeoutSeconds: 3,
            queueAdapterOverride: counter);
        await WaitForQueueReadyAsync(fixture, TimeSpan.FromSeconds(60));

        Guid orgId;
        Guid contributionId;
        Guid outboxId;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            (orgId, contributionId, outboxId) =
                await SeedCreatedContributionWithOutboxAsync(db);
        }

        var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        // First delivery: provider succeeds, contribution -> Succeeded, inbox
        // committed, then BeforeMessageAck throws BEFORE the SQS delete. The message
        // is left unacked (redelivery is forced).
        var crashedBeforeAck = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            var inboxCommitted = await db.Set<InboxMessage>()
                .IgnoreQueryFilters()
                .AnyAsync(x => x.MessageId == outboxId.ToString());
            var crashLogged = fixture.LogLines.Any(line =>
                line.Contains(
                    "Simulated crash at BeforeMessageAck",
                    StringComparison.Ordinal));
            return c?.State == ContributionState.Succeeded &&
                inboxCommitted &&
                crashLogged;
        }, TimeSpan.FromSeconds(60));
        Assert.True(
            crashedBeforeAck,
            "The worker did not commit and then fail before ACK. " +
            fixture.RecentLogs(50));
        Assert.Equal(0, counter.DeleteCount);
        Assert.Equal(1, counter.SendCount);
        Assert.Equal(1, counter.ReceiveCount);
        Assert.All(
            counter.ReceivedMessageIds,
            messageId => Assert.Equal(
                outboxId.ToString(),
                messageId));

        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
            Assert.Equal(ContributionState.Succeeded, contribution.State);

            // Exactly one provider operation on the first delivery.
            Assert.Equal(1, provider!.OperationCount);

            // Exactly one local provider reference.
            var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                .Where(r => r.ContributionId == contributionId).ToListAsync();
            Assert.Single(refs);

            // Exactly one inbox row for the processing message (the crash happened
            // after the inbox commit).
            var inboxes = await db.Set<InboxMessage>().IgnoreQueryFilters()
                .Where(m => m.MessageId == outboxId.ToString()).ToListAsync();
            Assert.Single(inboxes);

            var job = await db.Set<JobRun>()
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == outboxId);
            Assert.Equal(JobStatus.Succeeded, job.Status);

            // No dead letters from the simulated crash.
            var dead = await db.Set<DeadLetterRecord>().IgnoreQueryFilters().ToListAsync();
            Assert.Empty(dead);

            // No second successful attempt.
            var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
                .Where(a => a.ContributionId == contributionId).ToListAsync();
            Assert.Single(attempts);
        }

        output.WriteLine(
            "BEFORE ACK CRASH | MessageId={0} | " +
            "Contribution=Succeeded | Inbox=Processed | " +
            "JobRun=Succeeded | ProviderOperation=1 | " +
            "Attempt=1 | QueueDelete=0",
            outboxId);

        // Redelivery: visibility timeout expires, the SAME message is received again
        // (receiveCount >= 2) and the worker's inbox dedup deletes it (deleteCount >= 1).
        var redelivered = await WaitUntilAsync(
            () => Task.FromResult(counter.ReceiveCount >= 2 && counter.DeleteCount >= 1),
            TimeSpan.FromSeconds(60));
        Assert.True(redelivered, "Message was not redelivered and dedup-acked. " +
            $"ReceiveCount={counter.ReceiveCount}, DeleteCount={counter.DeleteCount}\n" + fixture.RecentLogs(40));

        Assert.True(counter.ReceiveCount >= 2, $"SqsReceiveCount >= 2 expected, got {counter.ReceiveCount}");
        Assert.True(
            counter.MaxApproximateReceiveCount >= 2,
            "SQS did not expose ApproximateReceiveCount >= 2.");
        Assert.All(
            counter.ReceivedMessageIds,
            messageId => Assert.Equal(
                outboxId.ToString(),
                messageId));

        // Queue eventually empty: the message was finally deleted/acked.
        var queueEmpty = await WaitUntilAsync(async () =>
        {
            using var scope = fixture.Host.Services.CreateScope();
            var adapter = scope.ServiceProvider.GetRequiredService<IQueueAdapter>();
            var qUrl = await adapter.GetOrCreateQueueAsync(fixture.QueueName);
            var leftover = await adapter.ReceiveAsync(qUrl, 1, CancellationToken.None);
            return leftover is null;
        }, TimeSpan.FromSeconds(30));
        Assert.True(queueEmpty, "Queue was not empty after dedup ack. " + fixture.RecentLogs(20));

        // The whole crash + redelivery + dedup cycle still had exactly one provider
        // effect.
        Assert.Equal(1, provider!.OperationCount);
        Assert.Equal(1, counter.SendCount);
        Assert.Equal(1, counter.DeleteCount);

        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
                .Where(a => a.ContributionId == contributionId).ToListAsync();
            Assert.Single(attempts);
            var inboxes = await db.Set<InboxMessage>()
                .IgnoreQueryFilters()
                .Where(x => x.MessageId == outboxId.ToString())
                .ToListAsync();
            Assert.Single(inboxes);
            var references = await db.Set<ProviderReference>()
                .IgnoreQueryFilters()
                .Where(x => x.ContributionId == contributionId)
                .ToListAsync();
            Assert.Single(references);
            var deadLetters = await db.Set<DeadLetterRecord>()
                .IgnoreQueryFilters()
                .CountAsync();
            Assert.Equal(0, deadLetters);
        }

        Assert.Contains(
            fixture.LogLines,
            line => line.Contains(
                "already processed (inbox dedup)",
                StringComparison.Ordinal));
        output.WriteLine(
            "REDELIVERY | SameMessageId={0} | ReceiveCount={1} | " +
            "ApproximateReceiveCount={2} | InboxDedup=true | Delete=1",
            outboxId,
            counter.ReceiveCount,
            counter.MaxApproximateReceiveCount);
        output.WriteLine(
            "FINAL | Queue=empty | Inbox=1 | Attempt=1 | " +
            "ProviderReference=1 | ProviderOperation=1 | " +
            "DeadLetter=0 | RESULT=PASS | StartedAt={0:O} | " +
            "CompletedAt={1:O}",
            startedAt,
            DateTime.UtcNow);
    }
}
