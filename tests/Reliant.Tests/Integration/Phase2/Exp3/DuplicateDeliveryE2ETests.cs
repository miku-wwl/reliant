using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

namespace Reliant.Tests.Integration.Phase2.Exp3;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public class DuplicateDeliveryE2ETests(ITestOutputHelper output)
{
    /// <summary>
    /// Records real queue operations and holds the first physical delivery until
    /// a second message with the same logical MessageId has also been received.
    /// The two ProcessingHandler tasks then enter the business path together.
    /// </summary>
    private sealed class ConcurrentDeliveryQueueAdapter(IQueueAdapter inner)
        : IQueueAdapter
    {
        private readonly TaskCompletionSource _twoInitialDeliveries =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _sentMessageIds = new();
        private readonly ConcurrentQueue<string> _receivedMessageIds = new();
        private int _receiveCount;
        private int _deleteCount;

        public int SendCount => _sentMessageIds.Count;
        public int ReceiveCount => _receiveCount;
        public int DeleteCount => _deleteCount;
        public IReadOnlyCollection<string> ReceivedMessageIds =>
            _receivedMessageIds.ToArray();

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

            if (message is null)
            {
                return null;
            }

            _receivedMessageIds.Enqueue(message.MessageId);
            var deliveryNumber = Interlocked.Increment(ref _receiveCount);

            if (deliveryNumber <= 2)
            {
                if (deliveryNumber == 2)
                {
                    _twoInitialDeliveries.TrySetResult();
                }

                await _twoInitialDeliveries.Task.WaitAsync(
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
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

    /// <summary>
    /// Blocks the first two handlers after both changed their in-memory state
    /// from Created to Processing but before either transaction is committed.
    /// Releasing them together forces an optimistic-concurrency race on Version.
    /// </summary>
    private sealed class ConcurrentInitialStateBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _twoArrivals =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivalCount;

        public int ArrivalCount => _arrivalCount;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var contributionEntry = eventData.Context?.ChangeTracker
                .Entries<Contribution>()
                .SingleOrDefault(entry =>
                    entry.State == EntityState.Modified &&
                    entry.Property(c => c.State).OriginalValue ==
                        ContributionState.Created &&
                    entry.Property(c => c.State).CurrentValue ==
                        ContributionState.Processing);

            if (contributionEntry is null)
            {
                return result;
            }

            var arrival = Interlocked.Increment(ref _arrivalCount);
            if (arrival > 2)
            {
                return result;
            }

            if (arrival == 2)
            {
                _twoArrivals.TrySetResult();
            }

            await _twoArrivals.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                cancellationToken);

            return result;
        }
    }

    /// <summary>
    /// Experiment 3 deliberately bypasses the newer Lease ownership gate so
    /// both deliveries still reach the Version concurrency boundary. Lease
    /// expiry and takeover are covered independently by Experiment 5.
    /// </summary>
    private sealed class PermissiveLeaseRepository : ILeaseRepository
    {
        private long _nextFencingToken;

        public Task<Lease?> GetActiveByJobRunAsync(
            Guid jobRunId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Lease?>(null);

        public Task<bool> TryAcquireAsync(
            Lease lease,
            CancellationToken cancellationToken = default)
        {
            lease.FencingToken =
                Interlocked.Increment(
                    ref _nextFencingToken);
            return Task.FromResult(true);
        }

        public Task<bool> TryLockCurrentOwnerAsync(
            JobExecutionFence fence,
            DateTime now,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task RenewAsync(
            Guid leaseId,
            DateTime newExpiresAt,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReleaseAsync(
            Guid leaseId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryReleaseExpiredAsync(
            Guid leaseId,
            DateTime now,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<List<Lease>> GetExpiredAsync(
            DateTime now,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<Lease>());
    }

    private sealed class PermissiveJobRunRepository : IJobRunRepository
    {
        public Task<JobRun?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
            => Task.FromResult<JobRun?>(new JobRun
            {
                Id = id,
                JobDefinitionId =
                    KnownJobDefinitions.ContributionProcessingId,
                QueueUrl =
                    KnownJobDefinitions.ContributionProcessingQueue,
                MessageId = id.ToString(),
                Payload = "{}",
                Status = JobStatus.Pending
            });

        public Task EnsurePendingAsync(
            JobRun jobRun,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AddAsync(
            JobRun jobRun,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateAsync(
            JobRun jobRun,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<List<JobRun>> GetByStatusAsync(
            Guid organizationId,
            JobStatus status,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<JobRun>());
    }

    private sealed class PermissiveJobAttemptRepository
        : IJobAttemptRepository
    {
        public Task<JobAttempt?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
            => Task.FromResult<JobAttempt?>(null);

        public Task<JobAttempt?> GetRunningByJobRunAsync(
            Guid jobRunId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<JobAttempt?>(null);

        public Task AddAsync(
            JobAttempt attempt,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
    public async Task SameMessageIdDeliveredConcurrently_ShouldCommitOneBusinessResult_AndExplainTheLoser()
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
        var queueProbe = new ConcurrentDeliveryQueueAdapter(
            new SqsQueueAdapter(queueConfiguration));
        var initialStateBarrier = new ConcurrentInitialStateBarrier();

        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeProcessing: true,
            includeReconciliation: false,
            visibilityTimeoutSeconds: 3,
            queueAdapterOverride: queueProbe,
            dbInterceptor: initialStateBarrier,
            leaseRepositoryOverride: new PermissiveLeaseRepository(),
            jobRunRepositoryOverride:
                new PermissiveJobRunRepository(),
            jobAttemptRepositoryOverride:
                new PermissiveJobAttemptRepository());

        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var outboxMessageId = Guid.NewGuid();
        var logicalMessageId = outboxMessageId.ToString();
        var correlationId = "phase2-exp3-duplicate-delivery";
        var payload = JsonSerializer.Serialize(new ContributionProcessingMessage(
            Version: 1,
            ContributionId: contributionId,
            OrganizationId: organizationId,
            Trigger: "Created",
            CorrelationId: correlationId));

        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            db.Organizations.Add(new Organization
            {
                Id = organizationId,
                Name = "Phase 2 Duplicate Delivery Lab",
                Status = OrganizationStatus.Active,
                Version = 0
            });
            db.Campaigns.Add(new Campaign
            {
                Id = campaignId,
                OrganizationId = organizationId,
                Name = "Experiment 3",
                Status = CampaignStatus.Active,
                Version = 0
            });
            db.Contributions.Add(new Contribution
            {
                Id = contributionId,
                OrganizationId = organizationId,
                CampaignId = campaignId,
                ExternalReference = "PHASE2-EXP3-001",
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

        // Two physical SQS messages, one stable logical Outbox MessageId.
        await Task.WhenAll(
            publisher.PublishAsync(
                fixture.QueueName,
                "ContributionCreated",
                payload,
                logicalMessageId),
            publisher.PublishAsync(
                fixture.QueueName,
                "ContributionCreated",
                payload,
                logicalMessageId));

        var converged = await WaitUntilAsync(async () =>
        {
            await using var db = CreateDbContext(fixture.PgConnectionString);
            var state = await db.Contributions.IgnoreQueryFilters()
                .Where(c => c.Id == contributionId)
                .Select(c => c.State)
                .SingleAsync();
            var inboxCount = await db.InboxMessages.IgnoreQueryFilters()
                .CountAsync(m => m.MessageId == logicalMessageId);
            var attemptCount = await db.ProcessingAttempts.IgnoreQueryFilters()
                .CountAsync(a => a.ContributionId == contributionId);

            var concurrentLoserLogged = fixture.LogLines.Any(line =>
                line.Contains(
                    "lost optimistic concurrency",
                    StringComparison.Ordinal));
            var redeliveryDedupLogged = fixture.LogLines.Any(line =>
                line.Contains(
                    "already processed (inbox dedup)",
                    StringComparison.Ordinal));

            return state == ContributionState.Succeeded
                && inboxCount == 1
                && attemptCount == 1
                && initialStateBarrier.ArrivalCount == 2
                && queueProbe.SendCount == 2
                && queueProbe.ReceiveCount >= 3
                && queueProbe.DeleteCount >= 2
                && concurrentLoserLogged
                && redeliveryDedupLogged;
        }, TimeSpan.FromSeconds(90));

        Assert.True(
            converged,
            "Concurrent duplicate delivery did not converge.\n" +
            $"BarrierArrivals={initialStateBarrier.ArrivalCount}, " +
            $"Send={queueProbe.SendCount}, " +
            $"Receive={queueProbe.ReceiveCount}, " +
            $"Delete={queueProbe.DeleteCount}\n" +
            fixture.RecentLogs(80));

        var provider = fixture.Host.Services.GetRequiredService<IProvider>()
            as SandboxProvider;
        Assert.NotNull(provider);

        int stateTransitionCount;
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
            var transitions = await db.StateTransitions.IgnoreQueryFilters()
                .Where(t => t.ContributionId == contributionId)
                .ToListAsync();
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
            Assert.Equal(0, deadLetters);

            Assert.Single(
                transitions,
                t => t.FromState == ContributionState.Created &&
                    t.ToState == ContributionState.Accepted);
            Assert.Single(
                transitions,
                t => t.FromState == ContributionState.Accepted &&
                    t.ToState == ContributionState.Processing);
            Assert.Single(
                transitions,
                t => t.FromState == ContributionState.Processing &&
                    t.ToState == ContributionState.Succeeded);
            Assert.Equal(3, transitions.Count);
            stateTransitionCount = transitions.Count;
        }

        var processingEntries = fixture.LogLines.Count(line =>
            line.Contains("Processing message", StringComparison.Ordinal));
        Assert.True(processingEntries >= 3);
        Assert.Equal(2, initialStateBarrier.ArrivalCount);
        Assert.Equal(2, queueProbe.SendCount);
        Assert.True(queueProbe.ReceiveCount >= 3);
        Assert.True(queueProbe.DeleteCount >= 2);
        Assert.All(
            queueProbe.ReceivedMessageIds,
            id => Assert.Equal(logicalMessageId, id));
        Assert.Equal(1, provider!.OperationCount);

        output.WriteLine(
            "CONCURRENT DELIVERY | OutboxId={0} | QueueSend={1} | " +
            "InitialConcurrentWorkers=2 | StateCommitBarrierArrivals={2}",
            outboxMessageId,
            queueProbe.SendCount,
            initialStateBarrier.ArrivalCount);
        output.WriteLine(
            "RECOVERY | QueueReceive={0} | QueueDelete={1} | " +
            "ConcurrentLoser='lost optimistic concurrency' | " +
            "Redelivery='already processed (inbox dedup)'",
            queueProbe.ReceiveCount,
            queueProbe.DeleteCount);
        output.WriteLine(
            "FINAL | Contributions=1 | BusinessState=Succeeded | " +
            "InboxRows=1 | ProcessingAttempts=1 | ProviderReferences=1 | " +
            "ProviderOperations=1 | StateTransitions={0} | DeadLetters=0",
            stateTransitionCount);
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O}",
            startedAt,
            DateTime.UtcNow);
    }
}
