using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;
using System.Text.Json;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public class SafeRetryE2ETests
{
    // Commit 16: the full safe-retry loop through real infrastructure:
    // TimeoutBeforeProcessing -> Unknown -> Reconciliation NotFound -> RetryPending
    // -> Retry Scheduler -> Outbox -> SQS -> Worker -> Provider Success -> Succeeded,
    // with exactly one provider effect.

    private static async Task<WorkerHostFixture> StartIsolatedWorkersAsync(string providerMode)
    {
        var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await fixture.StartWorkersAsync(providerMode: providerMode, includeReconciliation: true);
        await WaitForQueueReadyAsync(fixture, TimeSpan.FromSeconds(60));
        return fixture;
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
            Name = "Retry Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Set<Campaign>().Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = orgId,
            Name = "Retry",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Set<Contribution>().Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = orgId,
            CampaignId = campaignId,
            ExternalReference = "RETRY-001",
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
    public async Task TimeoutBeforeProcessing_ShouldReconcileNotFound_ThenRetryAndSucceed_WithOneProviderEffect()
    {
        await using var fixture = await StartIsolatedWorkersAsync("TimeoutBeforeProcessing");

        Guid orgId;
        Guid contributionId;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            (orgId, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
        }

        var control = fixture.Host.Services.GetRequiredService<ISandboxProviderControl>();
        var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        // Phase 1: the worker times out BEFORE processing, records Unknown, parks in
        // ReconciliationPending; reconciliation queries the provider by idempotency
        // key -> NotFound -> the contribution is parked in RetryPending.
        var retryPending = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.RetryPending;
        }, TimeSpan.FromSeconds(90));
        Assert.True(retryPending, "Contribution did not reach RetryPending via reconciliation. " + fixture.RecentLogs(60));

        // The timeout phase created NO provider-side operation.
        Assert.Equal(0, control.OperationCount);

        // Switch the provider so the retry succeeds.
        control.SetMode("Success");

        // Phase 2: the retry scheduler dispatches the due retry -> retry outbox ->
        // LocalStack -> worker consumes it (RetryPending -> Processing) -> provider
        // succeeds -> Succeeded.
        var succeeded = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.Succeeded;
        }, TimeSpan.FromSeconds(90));
        Assert.True(succeeded, "Contribution did not converge to Succeeded after the safe retry. " + fixture.RecentLogs(60));

        // Exactly one provider-side operation overall.
        Assert.Equal(1, control.OperationCount);

        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
            Assert.Equal(ContributionState.Succeeded, contribution.State);

            // The retry is fully consumed (no pending retry scheduled).
            Assert.Null(contribution.NextRetryAt);

            // A retry outbox message was durably written by the scheduler.
            var retryOutboxes = await db.Set<OutboxMessage>().IgnoreQueryFilters()
                .Where(m => m.OrganizationId == orgId && m.MessageType == "ContributionRetryRequested").ToListAsync();
            Assert.Single(retryOutboxes);

            // Two attempts: the timed-out attempt (Pending) and the successful retry.
            var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
                .Where(a => a.ContributionId == contributionId).ToListAsync();
            Assert.True(attempts.Count >= 2, $"Attempt count >= 2 expected, got {attempts.Count}");

            // Every attempt used the SAME provider idempotency key.
            var keys = attempts.Select(a => a.ProviderIdempotencyKey).Distinct().ToList();
            Assert.Single(keys);

            // Exactly one provider reference.
            var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                .Where(r => r.ContributionId == contributionId).ToListAsync();
            Assert.Single(refs);

            // No unresolved reconciliation remains.
            var unresolved = await db.Set<ReconciliationRecord>().IgnoreQueryFilters()
                .Where(r => r.ContributionId == contributionId && r.ResolvedAt == null).ToListAsync();
            Assert.Empty(unresolved);

            // No dead letters.
            var dead = await db.Set<DeadLetterRecord>().IgnoreQueryFilters().ToListAsync();
            Assert.Empty(dead);
        }
    }
}
