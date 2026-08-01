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
using Reliant.Tests.Integration.Fixtures;
using Reliant.Tests.TestHelpers;
using System.Text.Json;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public class CircuitOpenE2ETests
{
    // Commit 17: while the circuit is open the worker must NOT ack the SQS message
    // and must NOT write a processed inbox; the message redelivers (Approximate
    // Receive Count >= 2). After the circuit closes the redelivered message
    // processes successfully and the queue drains.

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
            Name = "CB Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Set<Campaign>().Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = orgId,
            Name = "CB",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Set<Contribution>().Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = orgId,
            CampaignId = campaignId,
            ExternalReference = "CB-001",
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

    [Fact]
    public async Task CircuitOpen_ShouldLeaveMessageUnacked_AndRedeliverAfterVisibilityTimeout()
    {
        // The worker itself uses a raw-SDK adapter that captures the SQS-native
        // ApproximateReceiveCount of every delivery, so no second consumer races it.
        var fixtureHost = new WorkerHostFixture();
        await using var fixture = fixtureHost;
        await fixture.InitializeAsync();
        var adapter = new RawSqsQueueAdapter(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Queue:Endpoint"] = fixture.SqsEndpoint,
                    ["Queue:Region"] = "us-west-1"
                })
                .Build());
        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeReconciliation: false,
            visibilityTimeoutSeconds: 3,
            queueAdapterOverride: adapter);
        await WaitForQueueReadyAsync(fixture, TimeSpan.FromSeconds(60));

        // Open the circuit breaker.
        var circuitBreaker = fixture.Host.Services.GetRequiredService<CircuitBreaker>();
        for (var i = 0; i < 5; i++)
        {
            circuitBreaker.RecordFailure(ErrorCategory.ServerError);
        }

        Guid contributionId;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            (_, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
        }

        var control = fixture.Host.Services.GetRequiredService<ISandboxProviderControl>();
        var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        // Phase A: while the circuit is open the worker defers - it leaves the
        // contribution in Processing, records no attempt, writes no inbox and does
        // not ack the message.
        var processing = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.Processing;
        }, TimeSpan.FromSeconds(60));
        Assert.True(processing, "Contribution did not reach Processing under circuit open. " + fixture.RecentLogs(30));

        // The message must redeliver (real visibility timeout) while unacked, and
        // SQS's native ApproximateReceiveCount must prove the redelivery.
        var redelivered = await WaitUntilAsync(
            () => Task.FromResult(adapter.MaxApproximateReceiveCount >= 2),
            TimeSpan.FromSeconds(60));
        Assert.True(redelivered,
            $"Message did not redeliver while circuit open. ReceiveCount={adapter.ReceiveCount}, ApproxReceiveCount={adapter.MaxApproximateReceiveCount}\n" +
            fixture.RecentLogs(40));

        // No ack happened yet.
        Assert.Equal(0, adapter.DeleteCount);

        // Open-phase assertions.
        Assert.Equal(0, control.OperationCount);
        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var c = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(x => x.Id == contributionId);
            Assert.Equal(ContributionState.Processing, c.State);
            Assert.Equal(0, c.RetryCount);

            var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
                .Where(a => a.ContributionId == contributionId).ToListAsync();
            Assert.Empty(attempts);

            var inboxes = await db.Set<InboxMessage>().IgnoreQueryFilters().ToListAsync();
            Assert.Empty(inboxes);
        }

        // Close the circuit -> the redelivered message now processes successfully.
        circuitBreaker.RecordSuccess();

        var succeeded = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.Succeeded;
        }, TimeSpan.FromSeconds(90));
        Assert.True(succeeded, "Contribution did not reach Succeeded after circuit closed. " + fixture.RecentLogs(40));

        // Recovered-phase assertions.
        Assert.Equal(1, control.OperationCount);
        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var c = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(x => x.Id == contributionId);
            Assert.Equal(ContributionState.Succeeded, c.State);

            var inboxes = await db.Set<InboxMessage>().IgnoreQueryFilters().ToListAsync();
            Assert.Single(inboxes);
        }

        // Queue eventually drains (message finally acked).
        var drained = await WaitUntilAsync(async () =>
        {
            using var scope = fixture.Host.Services.CreateScope();
            var adapter = scope.ServiceProvider.GetRequiredService<IQueueAdapter>();
            var qUrl = await adapter.GetOrCreateQueueAsync(fixture.QueueName);
            var leftover = await adapter.ReceiveAsync(qUrl, 1, CancellationToken.None);
            return leftover is null;
        }, TimeSpan.FromSeconds(30));
        Assert.True(drained, "Queue was not empty after circuit recovered. " + fixture.RecentLogs(20));
    }
}
