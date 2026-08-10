using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
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

namespace Reliant.Tests.Integration.Phase3.Exp2;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class TimeoutBeforeProcessingE2ETests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task TimeoutBeforeProcessing_ShouldWaitForNotFound_ThenRetryWithSameProviderKey()
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

        try
        {
            // Reconciliation is deliberately disabled so the test can inspect
            // the durable Unknown boundary before any provider query occurs.
            await fixture.StartWorkersAsync(
                providerMode: "TimeoutBeforeProcessing",
                includeReconciliation: false,
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

            var reachedUnknownBoundary = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var state = await db.Contributions
                        .IgnoreQueryFilters()
                        .Where(x => x.Id == contributionId)
                        .Select(x => x.State)
                        .SingleAsync();
                    return state ==
                            ContributionState.ReconciliationPending &&
                        countingQueue.DeleteCount == 1;
                },
                TimeSpan.FromSeconds(60));
            Assert.True(
                reachedUnknownBoundary,
                "The first attempt did not reach the durable " +
                "ReconciliationPending boundary." +
                Environment.NewLine +
                fixture.RecentLogs(100));

            var providerControl = fixture.Host.Services
                .GetRequiredService<ISandboxProviderControl>();
            Assert.Equal(0, providerControl.OperationCount);

            ProcessingAttempt firstAttempt;
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

                Assert.Equal(
                    ContributionState.ReconciliationPending,
                    contribution.State);
                Assert.Equal(0, contribution.RetryCount);
                Assert.Null(contribution.NextRetryAt);
                Assert.Equal(AttemptStatus.Unknown, firstAttempt.Status);
                Assert.Equal(1, firstAttempt.AttemptNumber);
                Assert.Equal(ErrorCategory.Timeout, firstAttempt.ErrorCategory);
                Assert.NotNull(firstAttempt.CompletedAt);
                Assert.Equal(
                    0,
                    await db.ProviderReferences
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.ReconciliationRecords
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.OutboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.OrganizationId == organizationId &&
                            x.MessageType ==
                                "ContributionRetryRequested"));

                var transitions = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .OrderBy(x => x.ChangedAt)
                    .ThenBy(x => x.Id)
                    .ToListAsync();
                Assert.Equal(5, transitions.Count);
                Assert.Equal(
                    (ContributionState.Processing,
                        ContributionState.ProviderUnknown),
                    (transitions[^2].FromState,
                        transitions[^2].ToState));
                Assert.Equal(
                    (ContributionState.ProviderUnknown,
                        ContributionState.ReconciliationPending),
                    (transitions[^1].FromState,
                        transitions[^1].ToState));
            }

            output.WriteLine(
                "UNKNOWN | State=ReconciliationPending | " +
                "Attempt=1/Unknown/Timeout | ProviderOperation=0 | " +
                "RetryCount=0 | NextRetryAt=null | RetryOutbox=0");

            ReconciliationResult reconciliationResult;
            using (var scope = fixture.Host.Services.CreateScope())
            {
                var sender = scope.ServiceProvider
                    .GetRequiredService<MediatR.ISender>();
                reconciliationResult = await sender.Send(
                    new ReconcileContributionCommand(contributionId));
            }

            Assert.True(reconciliationResult.Resolved);
            Assert.Equal("SafeRetry", reconciliationResult.Resolution);
            Assert.Equal(
                ReconciliationDifference.ProviderNotFound,
                reconciliationResult.Difference);
            Assert.Equal(0, providerControl.OperationCount);

            DateTime scheduledAt;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var record = await db.ReconciliationRecords
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);

                Assert.Equal(
                    ContributionState.RetryPending,
                    contribution.State);
                Assert.NotNull(contribution.NextRetryAt);
                scheduledAt = contribution.NextRetryAt!.Value;
                Assert.Equal("NotFound", record.ProviderState);
                Assert.Equal(
                    ReconciliationDifference.ProviderNotFound,
                    record.Difference);
                Assert.Equal("SafeRetry", record.Resolution);
                Assert.NotNull(record.ResolvedAt);
                Assert.Equal(
                    "ReconciliationHandler",
                    record.ResolvedBy);
            }

            output.WriteLine(
                "NOTFOUND | ProviderState=NotFound | " +
                "Resolution=SafeRetry | State=RetryPending | " +
                "NextRetryAt={0:O} | ProviderOperation=0",
                scheduledAt);

            // The provider recovers before the scheduled safe retry becomes
            // due. The maintenance scheduler still owns retry dispatch.
            providerControl.SetMode("Success");

            var succeeded = await WaitUntilAsync(
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
                        countingQueue.DeleteCount == 2;
                },
                TimeSpan.FromSeconds(90));
            Assert.True(
                succeeded,
                "The safe retry did not converge to Succeeded." +
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
                var retryOutboxes = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.OrganizationId == organizationId &&
                        x.MessageType ==
                            "ContributionRetryRequested")
                    .ToListAsync();
                var records = await db.ReconciliationRecords
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();

                Assert.Equal(ContributionState.Succeeded, contribution.State);
                Assert.Null(contribution.NextRetryAt);
                Assert.Collection(
                    attempts,
                    attempt =>
                    {
                        Assert.Equal(1, attempt.AttemptNumber);
                        Assert.Equal(AttemptStatus.Unknown, attempt.Status);
                    },
                    attempt =>
                    {
                        Assert.Equal(2, attempt.AttemptNumber);
                        Assert.Equal(AttemptStatus.Succeeded, attempt.Status);
                    });
                Assert.Single(
                    attempts.Select(x => x.ProviderIdempotencyKey)
                        .Distinct());
                Assert.Equal(
                    firstAttempt.ProviderIdempotencyKey,
                    attempts[1].ProviderIdempotencyKey);
                Assert.Single(references);
                Assert.Single(retryOutboxes);
                Assert.Equal(OutboxStatus.Sent, retryOutboxes[0].Status);
                Assert.Single(records);
                Assert.Equal("SafeRetry", records[0].Resolution);
                Assert.Equal(
                    0,
                    await db.DeadLetterRecords
                        .IgnoreQueryFilters()
                        .CountAsync());
            }

            Assert.Equal(1, providerControl.OperationCount);
            Assert.Equal(2, countingQueue.SendCount);
            Assert.Equal(2, countingQueue.ReceiveCount);
            Assert.Equal(2, countingQueue.DeleteCount);

            var queueUrl = await countingQueue
                .GetOrCreateQueueAsync(fixture.QueueName);
            Assert.Null(await countingQueue.ReceiveAsync(
                queueUrl,
                visibilityTimeoutSeconds: 0));

            output.WriteLine(
                "FINAL | State=Succeeded | Attempts=2 | " +
                "ProviderKeys=1 | ProviderOperation=1 | " +
                "ProviderReference=1 | RetryOutbox=1 | " +
                "QueueSend/Receive/Delete=2/2/2 | Queue=empty");
            output.WriteLine(
                "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
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
            correlationId: "phase3-exp2-timeout-before-processing");
        TenantFilterAccessor.SetOrganizationId(organizationId);
        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<MediatR.ISender>();
            return await sender.Send(new CreateContributionCommand(
                campaignId,
                ExternalReference: "PHASE3-EXP2-001",
                Amount: 125m,
                Currency: "NZD",
                IdempotencyKey: "phase3-exp2-create"));
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
            Name = "Phase 3 Experiment 2 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 2",
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
}
