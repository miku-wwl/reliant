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

namespace Reliant.Tests.Integration.Phase3.Exp6;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class WorkerCrashAfterProviderProcessedE2ETests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task CrashAfterProviderProcessed_ShouldRedeliverAndReplaySameProviderOperation()
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

        var countingQueue = new CountingQueueAdapter(
            new SqsQueueAdapter(CreateQueueConfiguration(
                fixture.SqsEndpoint,
                maxReceiveCount)));
        using var crash = new CrashAfterProviderProcessedFault();

        try
        {
            await fixture.StartWorkersAsync(
                providerMode: "Success",
                includeReconciliation: false,
                faultInjector: crash,
                visibilityTimeoutSeconds: 5,
                queueAdapterOverride: countingQueue,
                maxReceiveCount: maxReceiveCount,
                processingConcurrency: 1);

            var created = await CreateContributionAsync(
                fixture,
                organizationId,
                campaignId);
            Assert.Equal(201, created.StatusCode);
            Assert.NotNull(created.Body);
            var contributionId = created.Body!.Id;

            await crash.WaitUntilProviderProcessedAsync(
                TimeSpan.FromSeconds(60));

            var providerControl = fixture.Host.Services
                .GetRequiredService<ISandboxProviderControl>();
            Assert.Equal(1, providerControl.OperationCount);
            Assert.Equal(1, countingQueue.SendCount);
            Assert.Equal(1, countingQueue.ReceiveCount);
            Assert.Equal(0, countingQueue.DeleteCount);

            ProcessingAttempt firstAttempt;
            Guid originalMessageId;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                firstAttempt = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);
                originalMessageId = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == organizationId)
                    .Select(x => x.Id)
                    .SingleAsync();

                Assert.Equal(
                    ContributionState.Processing,
                    contribution.State);
                Assert.Equal(AttemptStatus.Pending, firstAttempt.Status);
                Assert.Equal(1, firstAttempt.AttemptNumber);
                Assert.Null(firstAttempt.ProviderReference);
                Assert.Null(firstAttempt.CompletedAt);
                Assert.Equal(
                    0,
                    await db.ProviderReferences
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.InboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.MessageId == originalMessageId.ToString()));
            }

            output.WriteLine(
                "AFTER PROVIDER | MessageId={0} | State=Processing | " +
                "Attempt=1/Pending | ProviderKey={1} | " +
                "ProviderOperation=1 | ProviderReference=0 | " +
                "Inbox=0 | Delete=0",
                originalMessageId,
                firstAttempt.ProviderIdempotencyKey);

            crash.ReleaseCrash();

            var recovered = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var state = await db.Contributions
                        .IgnoreQueryFilters()
                        .Where(x => x.Id == contributionId)
                        .Select(x => x.State)
                        .SingleAsync();
                    return state == ContributionState.Succeeded &&
                        countingQueue.ReceiveCount >= 2 &&
                        countingQueue.DeleteCount == 1;
                },
                TimeSpan.FromSeconds(90));
            Assert.True(
                recovered,
                "The redelivered message did not replay the original " +
                "provider operation and converge." +
                Environment.NewLine +
                fixture.RecentLogs(120));

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var attempts = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .OrderBy(x => x.AttemptNumber)
                    .ToListAsync();
                var references = await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();
                var inboxes = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.MessageId == originalMessageId.ToString())
                    .ToListAsync();
                var job = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == originalMessageId);
                var jobAttempts = await db.JobAttempts
                    .IgnoreQueryFilters()
                    .Where(x => x.JobRunId == originalMessageId)
                    .OrderBy(x => x.AttemptNumber)
                    .ToListAsync();
                var transitions = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();

                Assert.Equal(ContributionState.Succeeded, contribution.State);
                Assert.Collection(
                    attempts,
                    attempt =>
                    {
                        Assert.Equal(1, attempt.AttemptNumber);
                        Assert.Equal(AttemptStatus.Pending, attempt.Status);
                        Assert.Null(attempt.ProviderReference);
                    },
                    attempt =>
                    {
                        Assert.Equal(2, attempt.AttemptNumber);
                        Assert.Equal(AttemptStatus.Succeeded, attempt.Status);
                        Assert.NotNull(attempt.ProviderReference);
                    });
                Assert.Single(
                    attempts.Select(x => x.ProviderIdempotencyKey)
                        .Distinct());
                Assert.Equal(
                    firstAttempt.ProviderIdempotencyKey,
                    attempts[1].ProviderIdempotencyKey);
                Assert.Single(references);
                Assert.Equal(
                    attempts[1].ProviderReference,
                    references[0].Reference);
                Assert.Single(inboxes);
                Assert.Equal(InboxStatus.Processed, inboxes[0].Status);
                Assert.Equal(JobStatus.Succeeded, job.Status);
                Assert.Equal(2, job.AttemptCount);
                Assert.Collection(
                    jobAttempts,
                    attempt => Assert.Equal(
                        JobAttemptStatus.Failed,
                        attempt.Status),
                    attempt => Assert.Equal(
                        JobAttemptStatus.Succeeded,
                        attempt.Status));
                Assert.Equal(4, transitions.Count);
                Assert.Equal(
                    0,
                    await db.DeadLetterRecords
                        .IgnoreQueryFilters()
                        .CountAsync());
            }

            Assert.Equal(1, providerControl.OperationCount);
            Assert.Equal(1, countingQueue.SendCount);
            Assert.True(countingQueue.ReceiveCount >= 2);
            Assert.Equal(1, countingQueue.DeleteCount);

            var queueUrl = await countingQueue
                .GetOrCreateQueueAsync(fixture.QueueName);
            Assert.Null(await countingQueue.ReceiveAsync(
                queueUrl,
                visibilityTimeoutSeconds: 0));

            Assert.Contains(
                fixture.LogLines,
                line => line.Contains(
                    "Injected worker crash at AfterProviderProcessed",
                    StringComparison.Ordinal));
            output.WriteLine(
                "RECOVERY | ReceiveCount={0} | Attempts=2 | " +
                "DistinctProviderKeys=1 | Attempt2=Succeeded | " +
                "ProviderReference=1 | ProviderOperation=1",
                countingQueue.ReceiveCount);
            output.WriteLine(
                "FINAL | State=Succeeded | JobAttempts=Failed,Succeeded | " +
                "Inbox=1 | Delete=1 | Queue=empty | DeadLetter=0 | " +
                "RESULT=PASS | StartedAt={0:O} | CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
            crash.ReleaseCrash();
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
        using var scope = fixture.Host.Services.CreateScope();
        var tenantContext = scope.ServiceProvider
            .GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(
            organizationId,
            userId: null,
            role: null,
            correlationId: "phase3-exp6-worker-crash");
        TenantFilterAccessor.SetOrganizationId(organizationId);
        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<MediatR.ISender>();
            return await sender.Send(new CreateContributionCommand(
                campaignId,
                ExternalReference: "PHASE3-EXP6-001",
                Amount: 200m,
                Currency: "NZD",
                IdempotencyKey: "phase3-exp6-create"));
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private static async Task SeedOrganizationAndCampaignAsync(
        string connectionString,
        Guid organizationId,
        Guid campaignId)
    {
        await using var db = CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 3 Experiment 6 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 6",
            Status = CampaignStatus.Active,
            Version = 0
        });
        await db.SaveChangesAsync();
    }

    private static IConfiguration CreateQueueConfiguration(
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

    private sealed class CrashAfterProviderProcessedFault :
        IWorkerFaultInjector,
        IDisposable
    {
        private readonly TaskCompletionSource _providerProcessed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(false);
        private int _triggered;

        public void Inject(
            WorkerFaultPoint point,
            string contributionId)
        {
            if (point != WorkerFaultPoint.AfterProviderProcessed ||
                Interlocked.CompareExchange(ref _triggered, 1, 0) != 0)
            {
                return;
            }

            _providerProcessed.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(90)))
            {
                throw new TimeoutException(
                    "Exp6 crash boundary was not released.");
            }

            throw new InjectedWorkerCrashException(
                point,
                contributionId);
        }

        public Task WaitUntilProviderProcessedAsync(TimeSpan timeout)
            => _providerProcessed.Task.WaitAsync(timeout);

        public void ReleaseCrash()
            => _release.Set();

        public void Dispose()
        {
            ReleaseCrash();
            _release.Dispose();
        }
    }
}
