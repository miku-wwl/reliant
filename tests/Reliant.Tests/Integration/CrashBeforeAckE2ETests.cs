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
using System.Text.Json;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public class CrashBeforeAckE2ETests
{
    // Commit 15: crash AFTER the DB commit but BEFORE the SQS delete. The message
    // must redeliver (same SQS MessageId), the worker's inbox dedup must swallow it
    // without a second provider call, and the message must eventually be acked.

    private static ReliantDbContext CreateDbContext(string pgConnectionString)
    {
        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(pgConnectionString)
            .Options;
        return new ReliantDbContext(options);
    }

    private async Task<(Guid orgId, Guid contributionId)> SeedCreatedContributionWithOutboxAsync(ReliantDbContext db)
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
        db.Set<OutboxMessage>().Add(new OutboxMessage
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
        });

        await db.SaveChangesAsync();
        return (orgId, contributionId);
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
        var counter = new CountingQueueAdapter(new SqsQueueAdapter(innerConfig));

        await fixture.StartWorkersAsync(
            providerMode: "Success",
            faultInjector: new ThrowingFaultInjector(WorkerFaultPoint.BeforeMessageAck),
            includeReconciliation: false,
            visibilityTimeoutSeconds: 3,
            queueAdapterOverride: counter);
        await WaitForQueueReadyAsync(fixture, TimeSpan.FromSeconds(60));

        Guid orgId;
        Guid contributionId;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            (orgId, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
        }

        var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        // First delivery: provider succeeds, contribution -> Succeeded, inbox
        // committed, then BeforeMessageAck throws BEFORE the SQS delete. The message
        // is left unacked (redelivery is forced).
        var succeeded = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.Succeeded;
        }, TimeSpan.FromSeconds(60));
        Assert.True(succeeded, "Contribution did not reach Succeeded on first delivery. " + fixture.RecentLogs(30));

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
                .Where(m => m.OrganizationId == orgId).ToListAsync();
            Assert.Single(inboxes);

            // No dead letters from the simulated crash.
            var dead = await db.Set<DeadLetterRecord>().IgnoreQueryFilters().ToListAsync();
            Assert.Empty(dead);

            // No second successful attempt.
            var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
                .Where(a => a.ContributionId == contributionId).ToListAsync();
            Assert.Single(attempts);
        }

        // Redelivery: visibility timeout expires, the SAME message is received again
        // (receiveCount >= 2) and the worker's inbox dedup deletes it (deleteCount >= 1).
        var redelivered = await WaitUntilAsync(
            () => Task.FromResult(counter.ReceiveCount >= 2 && counter.DeleteCount >= 1),
            TimeSpan.FromSeconds(60));
        Assert.True(redelivered, "Message was not redelivered and dedup-acked. " +
            $"ReceiveCount={counter.ReceiveCount}, DeleteCount={counter.DeleteCount}\n" + fixture.RecentLogs(40));

        Assert.True(counter.ReceiveCount >= 2, $"SqsReceiveCount >= 2 expected, got {counter.ReceiveCount}");

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

        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
                .Where(a => a.ContributionId == contributionId).ToListAsync();
            Assert.Single(attempts);
        }
    }
}
