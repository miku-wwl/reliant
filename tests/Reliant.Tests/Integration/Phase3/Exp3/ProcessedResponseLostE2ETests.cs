using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Dto;
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
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp3;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class ProcessedResponseLostE2ETests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task ProcessedResponseLost_ShouldReconcileSucceeded_AndSuppressDuplicateBusinessMessage()
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
            // Freeze the workflow at ReconciliationPending. This lets the test
            // prove that the provider effect exists while the local reference
            // and final state do not yet exist.
            await fixture.StartWorkersAsync(
                providerMode: "ProcessedButResponseLost",
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

            var unknownCommitted = await WaitUntilAsync(
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
                unknownCommitted,
                "The response-lost attempt did not reach the durable " +
                "ReconciliationPending boundary." +
                Environment.NewLine +
                fixture.RecentLogs(100));

            var providerControl = fixture.Host.Services
                .GetRequiredService<ISandboxProviderControl>();
            Assert.Equal(1, providerControl.OperationCount);

            ProcessingAttempt unknownAttempt;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                unknownAttempt = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);

                Assert.Equal(
                    ContributionState.ReconciliationPending,
                    contribution.State);
                Assert.Equal(AttemptStatus.Unknown, unknownAttempt.Status);
                Assert.Equal(1, unknownAttempt.AttemptNumber);
                Assert.Equal(ErrorCategory.Timeout, unknownAttempt.ErrorCategory);
                Assert.Null(unknownAttempt.ProviderReference);
                Assert.False(string.IsNullOrWhiteSpace(
                    unknownAttempt.ProviderIdempotencyKey));
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
                "RESPONSE LOST | State=ReconciliationPending | " +
                "Attempt=1/Unknown/Timeout | ProviderOperation=1 | " +
                "AttemptReference=null | ProviderReferenceCount=0");

            ReconciliationResult reconciliationResult;
            using (var scope = fixture.Host.Services.CreateScope())
            {
                var sender = scope.ServiceProvider
                    .GetRequiredService<MediatR.ISender>();
                reconciliationResult = await sender.Send(
                    new ReconcileContributionCommand(contributionId));
            }

            Assert.True(reconciliationResult.Resolved);
            Assert.Equal("AutoFixed", reconciliationResult.Resolution);
            Assert.Equal(
                ReconciliationDifference.StateMismatch,
                reconciliationResult.Difference);
            Assert.Equal(1, providerControl.OperationCount);

            ProviderReference recoveredReference;
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
                recoveredReference = await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);

                Assert.Equal(ContributionState.Succeeded, contribution.State);
                Assert.Equal("Succeeded", record.ProviderState);
                Assert.Equal(
                    ReconciliationDifference.StateMismatch,
                    record.Difference);
                Assert.Equal("AutoFixed", record.Resolution);
                Assert.NotNull(record.ResolvedAt);
                Assert.Equal(
                    "ReconciliationHandler",
                    record.ResolvedBy);
                Assert.False(string.IsNullOrWhiteSpace(
                    recoveredReference.Reference));
                Assert.Equal("sandbox", recoveredReference.ProviderName);
            }

            output.WriteLine(
                "RECONCILED | QueryByKey=Succeeded | " +
                "Resolution=AutoFixed | State=Succeeded | " +
                "ProviderReference={0} | ProviderOperation=1",
                recoveredReference.Reference);

            var duplicateMessageId = await AddDuplicateBusinessMessageAsync(
                fixture.PgConnectionString,
                organizationId,
                contributionId);

            var duplicateAcknowledged = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var inboxExists = await db.InboxMessages
                        .IgnoreQueryFilters()
                        .AnyAsync(x =>
                            x.MessageId == duplicateMessageId.ToString());
                    return inboxExists && countingQueue.DeleteCount == 2;
                },
                TimeSpan.FromSeconds(60));
            Assert.True(
                duplicateAcknowledged,
                "The duplicate logical business message was not " +
                "idempotently acknowledged." +
                Environment.NewLine +
                fixture.RecentLogs(100));

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var attempts = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();
                var references = await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();
                var transitions = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();
                var duplicateJob = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == duplicateMessageId);
                var duplicateInbox = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.MessageId == duplicateMessageId.ToString());

                Assert.Equal(ContributionState.Succeeded, contribution.State);
                Assert.Single(attempts);
                Assert.Equal(AttemptStatus.Unknown, attempts[0].Status);
                Assert.Single(references);
                Assert.Equal(
                    recoveredReference.Reference,
                    references[0].Reference);
                Assert.Equal(6, transitions.Count);
                Assert.Single(
                    transitions,
                    x => x.ToState == ContributionState.Succeeded);
                Assert.Equal(JobStatus.Succeeded, duplicateJob.Status);
                Assert.Equal(InboxStatus.Processed, duplicateInbox.Status);
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
                "DUPLICATE | MessageId={0} | TerminalStateAck=true | " +
                "Attempts=1 | UnknownAttempts=1 | " +
                "ProviderReferences=1 | ProviderOperation=1",
                duplicateMessageId);
            output.WriteLine(
                "FINAL | State=Succeeded | QueueSend/Receive/Delete=2/2/2 | " +
                "Queue=empty | DeadLetter=0 | RESULT=PASS | " +
                "StartedAt={0:O} | CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private static async Task<Guid> AddDuplicateBusinessMessageAsync(
        string connectionString,
        Guid organizationId,
        Guid contributionId)
    {
        await using var db = CreateDbContext(connectionString);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MessageType = "ContributionCreated",
            Payload = JsonSerializer.Serialize(
                new ContributionProcessingMessage(
                    Version: 1,
                    ContributionId: contributionId,
                    OrganizationId: organizationId,
                    Trigger: "DuplicateAfterReconciliation",
                    CorrelationId: Guid.NewGuid().ToString())),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = DateTime.UtcNow,
            Status = OutboxStatus.Pending,
            Version = 0
        };
        db.OutboxMessages.Add(message);
        db.JobRuns.Add(JobRun.ForContributionProcessing(message));
        await db.SaveChangesAsync();
        return message.Id;
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
            correlationId: "phase3-exp3-processed-response-lost");
        TenantFilterAccessor.SetOrganizationId(organizationId);
        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<MediatR.ISender>();
            return await sender.Send(new CreateContributionCommand(
                campaignId,
                ExternalReference: "PHASE3-EXP3-001",
                Amount: 150m,
                Currency: "NZD",
                IdempotencyKey: "phase3-exp3-create"));
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
            Name = "Phase 3 Experiment 3 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 3",
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
