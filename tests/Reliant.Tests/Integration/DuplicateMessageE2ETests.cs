using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
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
public class DuplicateMessageE2ETests
{
    // Commit 14 distinguishes two DIFFERENT duplicate problems:
    //  - Scenario A: the SAME physical SQS message is redelivered (same SQS
    //    MessageId after visibility timeout). Protected by INBOX MessageId dedup.
    //  - Scenario B: a NEW SQS MessageId arrives for the SAME contribution.
    //    Protected by BUSINESS STATE (a terminal contribution is never
    //    reprocessed) plus provider idempotency / ProviderReference.
    // Both must keep the provider operation count at exactly one.

    private static async Task<WorkerHostFixture> StartIsolatedWorkersAsync(
        string providerMode,
        IWorkerFaultInjector? faultInjector = null,
        bool includeReconciliation = true,
        int visibilityTimeoutSeconds = 35)
    {
        var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await fixture.StartWorkersAsync(
            providerMode: providerMode,
            includeReconciliation: includeReconciliation,
            faultInjector: faultInjector,
            visibilityTimeoutSeconds: visibilityTimeoutSeconds);
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
            Name = "Dup Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Set<Campaign>().Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = orgId,
            Name = "Dup",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Set<Contribution>().Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = orgId,
            CampaignId = campaignId,
            ExternalReference = "DUP-001",
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

    private async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(500);
        }
        return await condition();
    }

    private async Task<bool> WaitForWorkerLogAsync(WorkerHostFixture fixture, string substring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (fixture.LogLines.Any(l => l.Contains(substring, StringComparison.Ordinal)))
                return true;
            await Task.Delay(300);
        }
        return fixture.LogLines.Any(l => l.Contains(substring, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ //
    // Scenario A: SAME physical SQS message redelivery (same MessageId).  //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task UnackedSameSqsMessage_ShouldRedeliverWithSameMessageId()
    {
        // Prove LocalStack's redelivery semantics: a message received but never
        // deleted becomes visible again after the visibility timeout and is
        // delivered with the SAME SQS MessageId (the same physical message).
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();

        var config = new AmazonSQSConfig
        {
            ServiceURL = fixture.SqsEndpoint,
            AuthenticationRegion = "us-west-1"
        };
        using var client = new AmazonSQSClient("test", "test", config);

        var queueUrl = (await client.CreateQueueAsync(new CreateQueueRequest { QueueName = fixture.QueueName })).QueueUrl;
        await client.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "redelivery-probe" });

        var first = (await client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            VisibilityTimeout = 2,
            WaitTimeSeconds = 0
        })).Messages.Single();

        // Do NOT delete -> visibility timeout (2s) expires -> redelivered.
        await Task.Delay(TimeSpan.FromSeconds(4));

        var second = (await client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            VisibilityTimeout = 2,
            WaitTimeSeconds = 0
        })).Messages.Single();

        Assert.Equal(first.MessageId, second.MessageId);

        await client.DeleteMessageAsync(queueUrl, second.ReceiptHandle);
    }

    [Fact]
    public async Task RedeliveredSameMessage_ShouldBeDeduplicatedByInbox()
    {
        // Real pipeline: the worker commits the inbox then faults BEFORE the SQS
        // delete (simulated crash). The message redelivers with the same SQS
        // MessageId; the INBOX dedup check must swallow it without reprocessing.
        await using var fixture = await StartIsolatedWorkersAsync(
            "Success",
            faultInjector: new ThrowingFaultInjector(WorkerFaultPoint.BeforeMessageAck),
            includeReconciliation: false,
            visibilityTimeoutSeconds: 3);

        Guid contributionId;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            (_, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
        }

        var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        // First delivery: provider succeeds, inbox is committed, BeforeMessageAck
        // throws, so the message is left unacked (redelivery is forced).
        var succeeded = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.Succeeded;
        }, TimeSpan.FromSeconds(60));
        Assert.True(succeeded, "Contribution did not reach Succeeded on first delivery. " + fixture.RecentLogs(30));

        Assert.Equal(1, provider!.OperationCount);

        // The worker must receive the redelivered (same MessageId) message and
        // swallow it via the inbox dedup path.
        var deduped = await WaitForWorkerLogAsync(fixture, "already processed (inbox dedup)", TimeSpan.FromSeconds(30));
        Assert.True(deduped, "Worker did not log inbox dedup for the redelivered message. " + fixture.RecentLogs(40));

        // Exactly ONE inbox row for the message despite two deliveries.
        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var inboxes = await db.Set<InboxMessage>().IgnoreQueryFilters().ToListAsync();
            Assert.Single(inboxes);
        }

        // No second provider effect from the redelivery.
        Assert.Equal(1, provider!.OperationCount);
    }

    [Fact]
    public async Task Redelivery_ShouldNotInvokeProviderAgain()
    {
        // Same redelivery scenario: prove the provider is invoked exactly once even
        // though the physical message was delivered twice.
        await using var fixture = await StartIsolatedWorkersAsync(
            "Success",
            faultInjector: new ThrowingFaultInjector(WorkerFaultPoint.BeforeMessageAck),
            includeReconciliation: false,
            visibilityTimeoutSeconds: 3);

        Guid contributionId;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            (_, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
        }

        var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        var succeeded = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.Succeeded;
        }, TimeSpan.FromSeconds(60));
        Assert.True(succeeded, "Contribution did not reach Succeeded on first delivery. " + fixture.RecentLogs(30));

        var deduped = await WaitForWorkerLogAsync(fixture, "already processed (inbox dedup)", TimeSpan.FromSeconds(30));
        Assert.True(deduped, "Worker did not log inbox dedup for the redelivered message. " + fixture.RecentLogs(40));

        // Wait a little for any (incorrect) second provider call to surface.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(1, provider!.OperationCount);
    }

    // --------------------------------------------------------------- //
    // Scenario B: NEW SQS MessageId for the SAME contribution.         //
    // --------------------------------------------------------------- //

    [Fact]
    public async Task NewMessageForSucceededContribution_ShouldNotInvokeProviderAgain()
    {
        // A brand new outbox message (new SQS MessageId) for an already-succeeded
        // contribution must NOT call the provider again - terminal business state
        // prevents reprocessing.
        await using var fixture = await StartIsolatedWorkersAsync("Success", includeReconciliation: false);

        Guid orgId;
        Guid contributionId;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            (orgId, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
        }

        var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        var succeeded = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.Succeeded;
        }, TimeSpan.FromSeconds(60));
        Assert.True(succeeded, "Contribution did not reach Succeeded. " + fixture.RecentLogs(30));

        Assert.Equal(1, provider!.OperationCount);

        // Second, brand new message for the SAME contribution.
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
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
        }

        // Wait until the second message is processed (its own inbox row is written
        // via the idempotent skip path).
        var secondProcessed = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var count = await db.Set<InboxMessage>().IgnoreQueryFilters().CountAsync();
            return count == 2;
        }, TimeSpan.FromSeconds(60));
        Assert.True(secondProcessed, "Second message was not processed/idempotently skipped. " + fixture.RecentLogs(40));

        // Provider was still invoked exactly once.
        Assert.Equal(1, provider!.OperationCount);

        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
            Assert.Equal(ContributionState.Succeeded, contribution.State);

            var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                .Where(r => r.ContributionId == contributionId).ToListAsync();
            Assert.Single(refs);
        }
    }

    [Fact]
    public async Task DifferentMessageIdSameContribution_ShouldBeProtectedByBusinessState()
    {
        // Explicit business-state protection: a new SQS MessageId for a terminal
        // contribution produces no state change, no provider effect, no duplicate
        // ProviderReference.
        await using var fixture = await StartIsolatedWorkersAsync("Success", includeReconciliation: false);

        Guid orgId;
        Guid contributionId;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            (orgId, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
        }

        var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        var succeeded = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
            return c?.State == ContributionState.Succeeded;
        }, TimeSpan.FromSeconds(60));
        Assert.True(succeeded, "Contribution did not reach Succeeded. " + fixture.RecentLogs(30));

        // Capture the state-transition count before the second message.
        int transitionsBefore;
        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            transitionsBefore = await db.Set<StateTransition>().IgnoreQueryFilters()
                .CountAsync(t => t.ContributionId == contributionId);
        }

        using (var db = CreateDbContext(fixture.PgConnectionString))
        {
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
        }

        var secondProcessed = await WaitUntilAsync(async () =>
        {
            using var db = CreateDbContext(fixture.PgConnectionString);
            var count = await db.Set<InboxMessage>().IgnoreQueryFilters().CountAsync();
            return count == 2;
        }, TimeSpan.FromSeconds(60));
        Assert.True(secondProcessed, "Second message was not processed/idempotently skipped. " + fixture.RecentLogs(40));

        await using (var db = CreateDbContext(fixture.PgConnectionString))
        {
            var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
            Assert.Equal(ContributionState.Succeeded, contribution.State);

            // Business state prevented any additional state change.
            var transitionsAfter = await db.Set<StateTransition>().IgnoreQueryFilters()
                .CountAsync(t => t.ContributionId == contributionId);
            Assert.Equal(transitionsBefore, transitionsAfter);

            var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                .Where(r => r.ContributionId == contributionId).ToListAsync();
            Assert.Single(refs);
        }

        Assert.Equal(1, provider!.OperationCount);
    }
}
