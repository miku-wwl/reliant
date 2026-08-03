using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using Reliant.Tests.TestHelpers;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp1;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public class PublisherCrashE2ETests(ITestOutputHelper output)
{
    private static ReliantDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ReliantDbContext(options);
    }

    private static IConfiguration CreateQueueConfiguration(WorkerHostFixture fixture)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:Endpoint"] = fixture.SqsEndpoint,
                ["Queue:Region"] = "us-west-1"
            })
            .Build();

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
    public async Task DbCommitted_PublisherStoppedBeforeSend_ShouldRecoverWithoutDuplicateBusinessResult()
    {
        var startedAt = DateTime.UtcNow;
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();

        var queueConfiguration = CreateQueueConfiguration(fixture);
        var realQueueAdapter = new SqsQueueAdapter(queueConfiguration);
        var pausedQueueAdapter = new PauseBeforeSendQueueAdapter(realQueueAdapter);

        // Run 1: the publisher is active, but the adapter pauses exactly before
        // the broker send. Processing is disabled because no queue message should
        // exist in this phase.
        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeProcessing: false,
            includeReconciliation: false,
            queueAdapterOverride: pausedQueueAdapter);

        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        Guid contributionId;

        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
            db.Organizations.Add(new Organization
            {
                Id = organizationId,
                Name = "Phase 2 Publisher Crash Lab",
                Status = OrganizationStatus.Active,
                Version = 0
            });
            db.Campaigns.Add(new Campaign
            {
                Id = campaignId,
                OrganizationId = organizationId,
                Name = "Experiment 1",
                Status = CampaignStatus.Active,
                Version = 0
            });
            await db.SaveChangesAsync();

            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(
                organizationId,
                userId: null,
                role: "student",
                correlationId: "phase2-exp1-publisher-crash");
            TenantFilterAccessor.SetOrganizationId(organizationId);

            try
            {
                var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
                var response = await sender.Send(new CreateContributionCommand(
                    campaignId,
                    ExternalReference: "PHASE2-EXP1-001",
                    Amount: 100m,
                    Currency: "NZD",
                    IdempotencyKey: "phase2-exp1-create-001"));

                Assert.Equal(201, response.StatusCode);
                Assert.NotNull(response.Body);
                contributionId = response.Body!.Id;
            }
            finally
            {
                TenantFilterAccessor.Clear();
            }
        }

        // Wait until OutboxPublisher has loaded the committed Pending row and has
        // reached the exact boundary before the real SQS SendMessage call.
        await pausedQueueAdapter.WaitUntilSendReachedAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, pausedQueueAdapter.SendAttempts);

        // This is the injected publisher termination. Stopping the host cancels
        // the paused SendAsync before it can delegate to the real SQS adapter.
        await fixture.StopWorkersAsync();
        var stoppedAt = DateTime.UtcNow;

        Guid outboxMessageId;
        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var businessRows = await db.Contributions.IgnoreQueryFilters()
                .Where(c => c.Id == contributionId)
                .ToListAsync();
            var outboxRows = await db.OutboxMessages.IgnoreQueryFilters()
                .Where(m => m.OrganizationId == organizationId
                    && m.MessageType == "ContributionCreated")
                .ToListAsync();

            Assert.Single(businessRows);
            Assert.Single(outboxRows);
            Assert.Equal(ContributionState.Created, businessRows[0].State);
            Assert.Equal(OutboxStatus.Pending, outboxRows[0].Status);
            Assert.Null(outboxRows[0].SentAt);
            Assert.Equal(0, outboxRows[0].SendCount);
            outboxMessageId = outboxRows[0].Id;
        }

        var queueUrl = await realQueueAdapter.GetOrCreateQueueAsync(fixture.QueueName);
        var messageBeforeRestart = await realQueueAdapter.ReceiveAsync(
            queueUrl,
            visibilityTimeoutSeconds: 1);
        Assert.Null(messageBeforeRestart);

        output.WriteLine(
            "BEFORE RESTART | ContributionId={0} | OutboxId={1} | " +
            "BusinessRows=1 | OutboxRows=1 | State=Created | " +
            "OutboxStatus=Pending | SentAt=null | QueueMessage=none",
            contributionId,
            outboxMessageId);

        // Run 2: use the real queue adapter, restart the Publisher and enable the
        // Processing Handler. The same durable Pending row must now publish and
        // produce one final business result.
        var recoveryCounter = new CountingQueueAdapter(
            new SqsQueueAdapter(queueConfiguration));
        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeProcessing: true,
            includeReconciliation: false,
            queueAdapterOverride: recoveryCounter);

        var recovered = await WaitUntilAsync(async () =>
        {
            await using var db = CreateDbContext(fixture.PgConnectionString);
            var outbox = await db.OutboxMessages.IgnoreQueryFilters()
                .SingleAsync(m => m.Id == outboxMessageId);
            var contribution = await db.Contributions.IgnoreQueryFilters()
                .SingleAsync(c => c.Id == contributionId);
            var inboxCount = await db.InboxMessages.IgnoreQueryFilters()
                .CountAsync(m => m.OrganizationId == organizationId
                    && m.HandlerName == "ProcessingHandler");

            return outbox.Status == OutboxStatus.Sent
                && contribution.State == ContributionState.Succeeded
                && inboxCount == 1
                && recoveryCounter.SendCount == 1
                && recoveryCounter.ReceiveCount >= 1
                && recoveryCounter.DeleteCount >= 1;
        }, TimeSpan.FromSeconds(60));

        Assert.True(
            recovered,
            "Publisher recovery did not converge. " +
            $"Send={recoveryCounter.SendCount}, " +
            $"Receive={recoveryCounter.ReceiveCount}, " +
            $"Delete={recoveryCounter.DeleteCount}\n" +
            fixture.RecentLogs(50));

        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var businessRows = await db.Contributions.IgnoreQueryFilters()
                .Where(c => c.Id == contributionId)
                .ToListAsync();
            var outboxRows = await db.OutboxMessages.IgnoreQueryFilters()
                .Where(m => m.Id == outboxMessageId)
                .ToListAsync();
            var inboxRows = await db.InboxMessages.IgnoreQueryFilters()
                .Where(m => m.OrganizationId == organizationId
                    && m.HandlerName == "ProcessingHandler")
                .ToListAsync();
            var attempts = await db.ProcessingAttempts.IgnoreQueryFilters()
                .Where(a => a.ContributionId == contributionId)
                .ToListAsync();
            var providerReferences = await db.ProviderReferences.IgnoreQueryFilters()
                .Where(r => r.ContributionId == contributionId)
                .ToListAsync();

            Assert.Single(businessRows);
            Assert.Equal(ContributionState.Succeeded, businessRows[0].State);
            Assert.Single(outboxRows);
            Assert.Equal(OutboxStatus.Sent, outboxRows[0].Status);
            Assert.NotNull(outboxRows[0].SentAt);
            Assert.Single(inboxRows);
            Assert.Single(attempts);
            Assert.Equal(AttemptStatus.Succeeded, attempts[0].Status);
            Assert.Single(providerReferences);
        }

        Assert.Equal(1, recoveryCounter.SendCount);

        output.WriteLine(
            "AFTER RESTART | ContributionId={0} | OutboxId={1} | " +
            "BusinessRows=1 | BusinessState=Succeeded | OutboxRows=1 | " +
            "OutboxStatus=Sent | InboxRows=1 | ProcessingAttempts=1 | " +
            "ProviderReferences=1 | QueueSend={2} | QueueReceive={3} | QueueDelete={4}",
            contributionId,
            outboxMessageId,
            recoveryCounter.SendCount,
            recoveryCounter.ReceiveCount,
            recoveryCounter.DeleteCount);
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | PublisherStoppedAt={1:O} | CompletedAt={2:O}",
            startedAt,
            stoppedAt,
            DateTime.UtcNow);
    }
}
