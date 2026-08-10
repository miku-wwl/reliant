using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Dto;
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

namespace Reliant.Tests.Integration.Phase3.Exp5;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class DifferentMessageIdSameContributionE2ETests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task NewMessageIdForSucceededContribution_ShouldAckWithoutNewBusinessEffect()
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
            await fixture.StartWorkersAsync(
                providerMode: "Success",
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

            var firstCompleted = await WaitUntilAsync(
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
                        countingQueue.DeleteCount == 1;
                },
                TimeSpan.FromSeconds(60));
            Assert.True(
                firstCompleted,
                "The original message did not complete." +
                Environment.NewLine +
                fixture.RecentLogs(80));

            var providerControl = fixture.Host.Services
                .GetRequiredService<ISandboxProviderControl>();
            Assert.Equal(1, providerControl.OperationCount);

            Guid originalMessageId;
            int transitionsBefore;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                originalMessageId = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.OrganizationId == organizationId &&
                        x.MessageType == "ContributionCreated")
                    .Select(x => x.Id)
                    .SingleAsync();
                transitionsBefore = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .CountAsync(x =>
                        x.ContributionId == contributionId);

                Assert.Equal(4, transitionsBefore);
                Assert.Equal(
                    1,
                    await db.ProcessingAttempts
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId == contributionId));
                Assert.Equal(
                    1,
                    await db.ProviderReferences
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId == contributionId));
                Assert.Equal(
                    1,
                    await db.InboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.OrganizationId == organizationId));
            }

            output.WriteLine(
                "ORIGINAL | MessageId={0} | State=Succeeded | " +
                "StateTransitions={1} | Attempt=1 | " +
                "ProviderReference=1 | ProviderOperation=1",
                originalMessageId,
                transitionsBefore);

            var duplicateMessageId = await AddDuplicateBusinessMessageAsync(
                fixture.PgConnectionString,
                organizationId,
                contributionId);
            Assert.NotEqual(originalMessageId, duplicateMessageId);

            var duplicateCompleted = await WaitUntilAsync(
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
                duplicateCompleted,
                "The new MessageId was not idempotently completed." +
                Environment.NewLine +
                fixture.RecentLogs(80));

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var outboxes = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == organizationId)
                    .OrderBy(x => x.OccurredAt)
                    .ToListAsync();
                var inboxes = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == organizationId)
                    .OrderBy(x => x.ProcessedAt)
                    .ToListAsync();
                var attempts = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();
                var references = await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();
                var transitionsAfter = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .CountAsync(x =>
                        x.ContributionId == contributionId);
                var jobs = await db.JobRuns
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.Id == originalMessageId ||
                        x.Id == duplicateMessageId)
                    .ToListAsync();

                Assert.Equal(ContributionState.Succeeded, contribution.State);
                Assert.Equal(2, outboxes.Count);
                Assert.All(
                    outboxes,
                    outbox => Assert.Equal(
                        OutboxStatus.Sent,
                        outbox.Status));
                Assert.Equal(2, inboxes.Count);
                Assert.Equal(
                    2,
                    inboxes.Select(x => x.MessageId)
                        .Distinct()
                        .Count());
                Assert.Contains(
                    originalMessageId.ToString(),
                    inboxes.Select(x => x.MessageId));
                Assert.Contains(
                    duplicateMessageId.ToString(),
                    inboxes.Select(x => x.MessageId));
                Assert.Single(attempts);
                Assert.Equal(AttemptStatus.Succeeded, attempts[0].Status);
                Assert.Single(references);
                Assert.Equal(transitionsBefore, transitionsAfter);
                Assert.Equal(2, jobs.Count);
                Assert.All(
                    jobs,
                    job => Assert.Equal(JobStatus.Succeeded, job.Status));
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

            Assert.Contains(
                fixture.LogLines,
                line => line.Contains(
                    "idempotent ACK without submit",
                    StringComparison.Ordinal));
            output.WriteLine(
                "NEW MESSAGE | MessageId={0} | SameContributionId={1} | " +
                "InboxRows=2 | JobRuns=2 | TerminalStateAck=true",
                duplicateMessageId,
                contributionId);
            output.WriteLine(
                "FINAL | State=Succeeded | StateTransitions={0} | " +
                "Attempts=1 | ProviderReferences=1 | " +
                "ProviderOperation=1 | QueueSend/Receive/Delete=2/2/2 | " +
                "Queue=empty | DeadLetter=0 | RESULT=PASS | " +
                "StartedAt={1:O} | CompletedAt={2:O}",
                transitionsBefore,
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
                    Trigger: "DuplicateLogicalMessage",
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
            correlationId: "phase3-exp5-different-message-id");
        TenantFilterAccessor.SetOrganizationId(organizationId);
        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<MediatR.ISender>();
            return await sender.Send(new CreateContributionCommand(
                campaignId,
                ExternalReference: "PHASE3-EXP5-001",
                Amount: 175m,
                Currency: "NZD",
                IdempotencyKey: "phase3-exp5-create"));
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
            Name = "Phase 3 Experiment 5 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 5",
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
