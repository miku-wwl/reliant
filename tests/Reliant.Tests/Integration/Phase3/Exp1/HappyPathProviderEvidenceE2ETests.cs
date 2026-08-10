using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Messaging;
using Reliant.Application.Tenancy;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using Reliant.Tests.TestHelpers;
using System.Globalization;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp1;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class HappyPathProviderEvidenceE2ETests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task HappyPath_ShouldPersistAttemptBeforeProvider_CommitInboxBeforeAck()
    {
        var startedAt = DateTime.UtcNow;
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        const int maxReceiveCount = 10;

        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await SeedOrganizationAndCampaignAsync(
            fixture.PgConnectionString,
            organizationId,
            campaignId);

        var queueConfiguration =
            CreateQueueConfiguration(
                fixture.SqsEndpoint,
                maxReceiveCount);
        var countingQueue = new CountingQueueAdapter(
            new SqsQueueAdapter(queueConfiguration));
        using var boundary =
            new EvidenceBoundaryFaultInjector();

        try
        {
            await fixture.StartWorkersAsync(
                providerMode: "Success",
                includeReconciliation: false,
                faultInjector: boundary,
                queueAdapterOverride: countingQueue,
                maxReceiveCount: maxReceiveCount,
                processingConcurrency: 1);

            var created = await CreateContributionAsync(
                fixture,
                organizationId,
                campaignId);
            Assert.Equal(201, created.StatusCode);
            Assert.False(created.WasCached);
            Assert.NotNull(created.Body);
            var contributionId = created.Body!.Id;

            OutboxMessage committedOutbox;
            JobRun committedJob;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var committedContribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                committedOutbox = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.OrganizationId == organizationId &&
                        x.Payload.Contains(
                            contributionId.ToString()));
                committedJob = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.Id == committedOutbox.Id);

                Assert.Equal(
                    ContributionState.Created,
                    committedContribution.State);
                Assert.Equal(
                    OutboxStatus.Pending,
                    committedOutbox.Status);
                Assert.Equal(
                    JobStatus.Pending,
                    committedJob.Status);
            }

            await boundary.WaitForAttemptPersistedAsync(
                TimeSpan.FromSeconds(60));

            var provider = fixture.Host.Services
                .GetRequiredService<IProvider>() as
                    SandboxProvider;
            Assert.NotNull(provider);
            Assert.Equal(0, provider.OperationCount);
            Assert.Equal(0, countingQueue.DeleteCount);

            ProcessingAttempt pendingAttempt;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                pendingAttempt = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);

                Assert.Equal(
                    AttemptStatus.Pending,
                    pendingAttempt.Status);
                Assert.Equal(1, pendingAttempt.AttemptNumber);
                Assert.False(string.IsNullOrWhiteSpace(
                    pendingAttempt.ProviderIdempotencyKey));
                Assert.Equal(
                    ContributionState.Processing,
                    contribution.State);
                Assert.Equal(
                    0,
                    await db.ProviderReferences
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId ==
                                contributionId));
                Assert.Equal(
                    0,
                    await db.InboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.MessageId ==
                                committedOutbox.Id.ToString()));
            }

            output.WriteLine(
                "BEFORE PROVIDER | Contribution={0} | " +
                "Outbox={1} | Attempt=Pending | " +
                "AttemptNumber=1 | ProviderKey={2} | " +
                "ProviderOperationCount=0 | Inbox=0 | ACK=0",
                contributionId,
                committedOutbox.Id,
                pendingAttempt.ProviderIdempotencyKey);

            boundary.ReleaseProviderCall();
            await boundary.WaitForBeforeAckAsync(
                TimeSpan.FromSeconds(60));

            Assert.Equal(1, provider.OperationCount);
            Assert.Equal(0, countingQueue.DeleteCount);

            var beforeAck = await ReadSnapshotAsync(
                fixture.PgConnectionString,
                contributionId,
                committedOutbox.Id);
            Assert.Equal(
                ContributionState.Succeeded,
                beforeAck.Contribution.State);
            Assert.Equal(
                OutboxStatus.Sent,
                beforeAck.Outbox.Status);
            Assert.Equal(
                JobStatus.Succeeded,
                beforeAck.JobRun.Status);
            Assert.Single(beforeAck.ProcessingAttempts);
            Assert.Equal(
                AttemptStatus.Succeeded,
                beforeAck.ProcessingAttempts[0].Status);
            Assert.NotNull(
                beforeAck.ProcessingAttempts[0].CompletedAt);
            Assert.Single(beforeAck.ProviderReferences);
            Assert.Equal(
                beforeAck.ProcessingAttempts[0]
                    .ProviderReference,
                beforeAck.ProviderReferences[0].Reference);
            Assert.Single(beforeAck.InboxMessages);
            Assert.Equal(
                InboxStatus.Processed,
                beforeAck.InboxMessages[0].Status);
            Assert.Equal(
                committedOutbox.Id.ToString(),
                beforeAck.InboxMessages[0].MessageId);
            Assert.Equal(4, beforeAck.StateTransitions.Count);
            Assert.Collection(
                beforeAck.StateTransitions,
                transition => Assert.Equal(
                    (ContributionState.Created,
                        ContributionState.Created),
                    (transition.FromState,
                        transition.ToState)),
                transition => Assert.Equal(
                    (ContributionState.Created,
                        ContributionState.Accepted),
                    (transition.FromState,
                        transition.ToState)),
                transition => Assert.Equal(
                    (ContributionState.Accepted,
                        ContributionState.Processing),
                    (transition.FromState,
                        transition.ToState)),
                transition => Assert.Equal(
                    (ContributionState.Processing,
                        ContributionState.Succeeded),
                    (transition.FromState,
                        transition.ToState)));
            Assert.Single(beforeAck.JobAttempts);
            Assert.Equal(
                JobAttemptStatus.Succeeded,
                beforeAck.JobAttempts[0].Status);
            Assert.Single(beforeAck.Leases);
            Assert.True(beforeAck.Leases[0].IsActive);
            Assert.Equal(0, beforeAck.DeadLetterCount);

            output.WriteLine(
                "BEFORE ACK | Contribution=Succeeded | " +
                "Attempt=Succeeded | ProviderOperationCount=1 | " +
                "ProviderReference=1 | Inbox=Processed | " +
                "StateTransitions=4 | JobRun=Succeeded | ACK=0");

            boundary.ReleaseAck();
            var acknowledged = await WaitUntilAsync(
                async () =>
                {
                    if (countingQueue.DeleteCount != 1)
                    {
                        return false;
                    }

                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    return !await db.Leases
                        .IgnoreQueryFilters()
                        .Where(x =>
                            x.JobRunId == committedOutbox.Id)
                        .Select(x => x.IsActive)
                        .SingleAsync();
                },
                TimeSpan.FromSeconds(30));
            Assert.True(
                acknowledged,
                "The worker did not ACK and release its Lease." +
                Environment.NewLine +
                fixture.RecentLogs(100));

            var queueUrl = await countingQueue
                .GetOrCreateQueueAsync(fixture.QueueName);
            var queueEmpty = await WaitUntilAsync(
                async () =>
                    await countingQueue.ReceiveAsync(
                        queueUrl,
                        visibilityTimeoutSeconds: 0,
                        CancellationToken.None) is null,
                TimeSpan.FromSeconds(15));
            Assert.True(queueEmpty);
            Assert.Equal(1, countingQueue.SendCount);
            Assert.Equal(1, countingQueue.ReceiveCount);
            Assert.Equal(1, countingQueue.DeleteCount);

            var keyFactory = fixture.Host.Services
                .GetRequiredService<IProviderOperationKeyFactory>();
            var expectedProviderKey =
                keyFactory.CreateContributionSubmitKey(
                    organizationId,
                    contributionId,
                    "sandbox");
            Assert.Equal(
                expectedProviderKey,
                beforeAck.ProcessingAttempts[0]
                    .ProviderIdempotencyKey);

            output.WriteLine(
                "FINAL | QueueSend=1 | QueueReceive=1 | " +
                "QueueDelete=1 | Queue=empty | Lease=inactive | " +
                "ProviderOperationCount=1 | " +
                "ProviderReferenceCount=1 | InboxCount=1");
            output.WriteLine(
                "RESULT | PASS | StartedAt={0:O} | " +
                "CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
            boundary.ReleaseAll();
            TenantFilterAccessor.Clear();
        }
    }

    private static async Task<
        Reliant.Application.Dto.IdempotentResponse<
            Reliant.Application.Dto.ContributionResponse>>
        CreateContributionAsync(
            WorkerHostFixture fixture,
            Guid organizationId,
            Guid campaignId)
    {
        using var scope =
            fixture.Host.Services.CreateScope();
        var tenantContext = scope.ServiceProvider
            .GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(
            organizationId,
            userId: null,
            role: null,
            correlationId:
                "phase3-exp1-happy-path");
        TenantFilterAccessor.SetOrganizationId(
            organizationId);
        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<MediatR.ISender>();
            return await sender.Send(
                new CreateContributionCommand(
                    campaignId,
                    ExternalReference:
                        "PHASE3-EXP1-001",
                    Amount: 100m,
                    Currency: "NZD",
                    IdempotencyKey:
                        "phase3-exp1-create"));
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private static async Task
        SeedOrganizationAndCampaignAsync(
            string connectionString,
            Guid organizationId,
            Guid campaignId)
    {
        await using var db =
            CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 3 Experiment 1 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 1",
            Status = CampaignStatus.Active,
            Version = 0
        });
        await db.SaveChangesAsync();
    }

    private static async Task<FinalSnapshot>
        ReadSnapshotAsync(
            string connectionString,
            Guid contributionId,
            Guid outboxId)
    {
        await using var db =
            CreateDbContext(connectionString);
        return new FinalSnapshot(
            Contribution:
                await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.Id == contributionId),
            Outbox:
                await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.Id == outboxId),
            JobRun:
                await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.Id == outboxId),
            ProcessingAttempts:
                await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.ContributionId ==
                            contributionId)
                    .OrderBy(x => x.AttemptNumber)
                    .ToListAsync(),
            ProviderReferences:
                await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.ContributionId ==
                            contributionId)
                    .ToListAsync(),
            InboxMessages:
                await db.InboxMessages
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.MessageId == outboxId.ToString())
                    .ToListAsync(),
            StateTransitions:
                await db.StateTransitions
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.ContributionId ==
                            contributionId)
                    .OrderBy(x => x.ChangedAt)
                    .ThenBy(x => x.Id)
                    .ToListAsync(),
            JobAttempts:
                await db.JobAttempts
                    .IgnoreQueryFilters()
                    .Where(x => x.JobRunId == outboxId)
                    .ToListAsync(),
            Leases:
                await db.Leases
                    .IgnoreQueryFilters()
                    .Where(x => x.JobRunId == outboxId)
                    .ToListAsync(),
            DeadLetterCount:
                await db.DeadLetterRecords
                    .IgnoreQueryFilters()
                    .CountAsync());
    }

    private static IConfiguration
        CreateQueueConfiguration(
            string endpoint,
            int maxReceiveCount)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Queue:Endpoint"] = endpoint,
                    ["Queue:Region"] = "us-west-1",
                    ["Queue:MaxReceiveCount"] =
                        maxReceiveCount.ToString(
                            CultureInfo.InvariantCulture)
                })
            .Build();

    private static ReliantDbContext CreateDbContext(
        string connectionString)
    {
        var options =
            new DbContextOptionsBuilder<ReliantDbContext>()
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

            await Task.Delay(250);
        }

        return await condition();
    }

    private sealed class EvidenceBoundaryFaultInjector :
        IWorkerFaultInjector,
        IDisposable
    {
        private readonly TaskCompletionSource
            _attemptPersisted = new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        private readonly TaskCompletionSource
            _beforeAck = new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim
            _providerRelease = new(false);
        private readonly ManualResetEventSlim
            _ackRelease = new(false);

        public void Inject(
            WorkerFaultPoint point,
            string contributionId)
        {
            switch (point)
            {
                case WorkerFaultPoint
                    .AfterAttemptPersisted:
                    _attemptPersisted.TrySetResult();
                    if (!_providerRelease.Wait(
                        TimeSpan.FromSeconds(90)))
                    {
                        throw new TimeoutException(
                            "Exp1 provider boundary was not released.");
                    }
                    break;

                case WorkerFaultPoint.BeforeMessageAck:
                    _beforeAck.TrySetResult();
                    if (!_ackRelease.Wait(
                        TimeSpan.FromSeconds(90)))
                    {
                        throw new TimeoutException(
                            "Exp1 ACK boundary was not released.");
                    }
                    break;
            }
        }

        public Task WaitForAttemptPersistedAsync(
            TimeSpan timeout)
            => _attemptPersisted.Task.WaitAsync(timeout);

        public Task WaitForBeforeAckAsync(
            TimeSpan timeout)
            => _beforeAck.Task.WaitAsync(timeout);

        public void ReleaseProviderCall()
            => _providerRelease.Set();

        public void ReleaseAck()
            => _ackRelease.Set();

        public void ReleaseAll()
        {
            _providerRelease.Set();
            _ackRelease.Set();
        }

        public void Dispose()
        {
            ReleaseAll();
            _providerRelease.Dispose();
            _ackRelease.Dispose();
        }
    }

    private sealed record FinalSnapshot(
        Contribution Contribution,
        OutboxMessage Outbox,
        JobRun JobRun,
        List<ProcessingAttempt> ProcessingAttempts,
        List<ProviderReference> ProviderReferences,
        List<InboxMessage> InboxMessages,
        List<StateTransition> StateTransitions,
        List<JobAttempt> JobAttempts,
        List<Lease> Leases,
        int DeadLetterCount);
}
