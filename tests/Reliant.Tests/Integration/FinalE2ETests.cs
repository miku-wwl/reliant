using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
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
public class FinalE2ETests
{
    // NOTE: each test starts its OWN WorkerHostFixture (own PostgreSQL + LocalStack
    // containers + worker host) so the two E2E scenarios are fully isolated - a
    // shared host left in-flight background tasks that consumed the other test's
    // messages, stalling convergence.

    private static async Task<WorkerHostFixture> StartIsolatedWorkersAsync(
        string providerMode, bool includeReconciliation = true)
    {
        var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await fixture.StartWorkersAsync(providerMode: providerMode, includeReconciliation: includeReconciliation);
        await WaitForQueueReadyAsync(fixture, TimeSpan.FromSeconds(60));
        return fixture;
    }

    /// <summary>
    /// The LocalStack container can report healthy before SQS is actually
    /// reachable. Wait until the processing queue can be created/queried so the
    /// Outbox Publisher and Processing Handler can deliver the message.
    /// </summary>
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
            Name = "E2E Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Set<Campaign>().Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = orgId,
            Name = "E2E",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Set<Contribution>().Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = orgId,
            CampaignId = campaignId,
            ExternalReference = "E2E-001",
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

    private async Task<string> DescribeStateAsync(string pgConnectionString, Guid contributionId, SandboxProvider? provider)
    {
        await using var db = CreateDbContext(pgConnectionString);
        var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
        var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
            .Where(a => a.ContributionId == contributionId).Select(a => $"{a.AttemptNumber}:{a.Status}").ToListAsync();
        var inboxes = await db.Set<InboxMessage>().IgnoreQueryFilters()
            .Where(m => m.OrganizationId == c!.OrganizationId).Select(m => m.MessageId).ToListAsync();
        var dead = await db.Set<DeadLetterRecord>().IgnoreQueryFilters().CountAsync();
        return $"State={c?.State}, ProviderOp={provider?.OperationCount}, " +
               $"Attempts=[{string.Join(",", attempts)}], Inbox=[{string.Join(",", inboxes)}], DeadLetters={dead}";
    }

    [Fact]
    public async Task ProcessedResponseLost_WithDuplicateMessageAndCallback_ShouldConverge_WithoutSecondProviderEffect()
    {
        // Provider processes the operation but its response is lost -> Unknown ->
        // reconciliation must converge to Succeeded with exactly one provider effect.
        await using var fixture = await StartIsolatedWorkersAsync("ProcessedButResponseLost");
        try
        {
            Guid orgId;
            Guid contributionId;
            using (var db = CreateDbContext(fixture.PgConnectionString))
            {
                (orgId, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
            }

            var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
            Assert.NotNull(provider);

            // 1) Real pipeline: Outbox -> SQS -> Worker -> Provider -> Reconciliation.
            var converged = await WaitUntilAsync(async () =>
            {
                using var db = CreateDbContext(fixture.PgConnectionString);
                var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
                return c?.State == ContributionState.Succeeded;
            }, TimeSpan.FromSeconds(90));

            Assert.True(converged, "Contribution did not converge to Succeeded within timeout. " +
                await DescribeStateAsync(fixture.PgConnectionString, contributionId, provider) +
                $"\nSeededContributionId={contributionId}, SqsEndpoint={fixture.SqsEndpoint}, Queue={fixture.QueueName}\n" +
                "\nWORKER LOGS:\n" + fixture.RecentLogs(60));

            using (var db = CreateDbContext(fixture.PgConnectionString))
            {
                var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
                Assert.Equal(ContributionState.Succeeded, contribution.State);

                // Exactly one provider-side operation.
                Assert.Equal(1, provider!.OperationCount);

                // Exactly one local provider reference.
                var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                    .Where(r => r.ContributionId == contributionId).ToListAsync();
                Assert.Single(refs);

                // The full state path was audited with one transition per change:
                // Created->Accepted, Accepted->Processing, Processing->ProviderUnknown,
                // ProviderUnknown->ReconciliationPending, ReconciliationPending->Succeeded.
                var transitions = await db.Set<StateTransition>().IgnoreQueryFilters()
                    .Where(t => t.ContributionId == contributionId).ToListAsync();
                Assert.Contains(transitions, t => t.FromState == ContributionState.Created && t.ToState == ContributionState.Accepted);
                Assert.Contains(transitions, t => t.FromState == ContributionState.Accepted && t.ToState == ContributionState.Processing);
                Assert.Contains(transitions, t => t.FromState == ContributionState.Processing && t.ToState == ContributionState.ProviderUnknown);
                Assert.Contains(transitions, t => t.FromState == ContributionState.ProviderUnknown && t.ToState == ContributionState.ReconciliationPending);
                Assert.Contains(transitions, t => t.FromState == ContributionState.ReconciliationPending && t.ToState == ContributionState.Succeeded);

                // No unresolved reconciliation remains.
                var unresolved = await db.Set<ReconciliationRecord>().IgnoreQueryFilters()
                    .Where(r => r.ContributionId == contributionId && r.ResolvedAt == null).ToListAsync();
                Assert.Empty(unresolved);

                // No dead letters.
                var deadLetters = await db.Set<DeadLetterRecord>().IgnoreQueryFilters().ToListAsync();
                Assert.Empty(deadLetters);
            }

            // 2) Duplicate callback: the SAME EventId is delivered twice. Only the
            //    first may apply; the second is an idempotent terminal confirmation.
            using (var scope = fixture.Host.Services.CreateScope())
            {
                var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
                var keyFactory = scope.ServiceProvider.GetRequiredService<IProviderOperationKeyFactory>();
                var idempotencyKey = keyFactory.CreateContributionSubmitKey(orgId, contributionId, "sandbox");

                var command = new HandleProviderCallbackCommand(new ProviderCallbackPayload(
                    EventId: "e2e-callback-1",
                    EventType: "contribution.submit",
                    ProviderReference: null,
                    IdempotencyKey: idempotencyKey,
                    Status: "succeeded",
                    OccurredAt: DateTime.UtcNow.ToString("O"),
                    Version: 1));

                var first = await sender.Send(command);
                var duplicate = await sender.Send(command);

                Assert.Equal(200, first.StatusCode);
                Assert.Equal(200, duplicate.StatusCode);
                Assert.Equal(1, provider!.OperationCount);

                await using var db = CreateDbContext(fixture.PgConnectionString);
                var callbackInboxes = await db.Set<InboxMessage>().IgnoreQueryFilters()
                    .Where(m => m.MessageId == "callback-e2e-callback-1").ToListAsync();
                Assert.Single(callbackInboxes);

                // The contribution is already Succeeded (via reconciliation), so the
                // duplicate terminal confirmation must create NO additional state change.
                var callbackTransitions = await db.Set<StateTransition>().IgnoreQueryFilters()
                    .Where(t => t.ContributionId == contributionId
                        && t.ToState == ContributionState.Succeeded
                        && t.Reason.Contains("Callback"))
                    .ToListAsync();
                Assert.Empty(callbackTransitions);
            }

            // 3) A new logical message for the same Contribution is suppressed by
            //    terminal business-state protection and provider idempotency.
            //    (Same-physical-MessageId redelivery is covered separately by
            //    Phase3 Exp4; different-message business dedup by Exp5.)
            using (var db = CreateDbContext(fixture.PgConnectionString))
            {
                var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
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

            // Give the duplicate message a chance to flow; the provider effect must
            // remain exactly one.
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.Equal(1, provider!.OperationCount);

            using (var db = CreateDbContext(fixture.PgConnectionString))
            {
                var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                    .Where(r => r.ContributionId == contributionId).ToListAsync();
                Assert.Single(refs);
            }
        }
        finally
        {
            // The fixture is disposed via await using; nothing else to stop.
        }
    }

    [Fact]
    public async Task CallbackBeforeReconciliation_WithDuplicateEvent_ShouldConvergeOnce()
    {
        // The callback (same EventId twice) moves the contribution to Succeeded
        // BEFORE reconciliation runs; a later reconciliation must not overwrite it
        // and the duplicate callback must not create a second state change.
        await using var fixture = await StartIsolatedWorkersAsync("ProcessedButResponseLost", includeReconciliation: false);
        try
        {
            Guid orgId;
            Guid contributionId;
            using (var db = CreateDbContext(fixture.PgConnectionString))
            {
                (orgId, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
            }

            var provider = fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
            Assert.NotNull(provider);

            // Wait until the worker has recorded the unknown outcome and parked the
            // contribution in ReconciliationPending (reconciliation is disabled).
            var parked = await WaitUntilAsync(async () =>
            {
                using var db = CreateDbContext(fixture.PgConnectionString);
                var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
                return c?.State == ContributionState.ReconciliationPending;
            }, TimeSpan.FromSeconds(90));
            Assert.True(parked, "Contribution did not reach ReconciliationPending. " +
                await DescribeStateAsync(fixture.PgConnectionString, contributionId, provider) +
                "\n\nWORKER LOGS:\n" + fixture.RecentLogs(60));

            using (var scope = fixture.Host.Services.CreateScope())
            {
                var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
                var keyFactory = scope.ServiceProvider.GetRequiredService<IProviderOperationKeyFactory>();
                var idempotencyKey = keyFactory.CreateContributionSubmitKey(orgId, contributionId, "sandbox");

                var command = new HandleProviderCallbackCommand(new ProviderCallbackPayload(
                    EventId: "e2e-callback-before-recon",
                    EventType: "contribution.submit",
                    ProviderReference: null,
                    IdempotencyKey: idempotencyKey,
                    Status: "succeeded",
                    OccurredAt: DateTime.UtcNow.ToString("O"),
                    Version: 1));

                var first = await sender.Send(command);
                var duplicate = await sender.Send(command);
                Assert.Equal(200, first.StatusCode);
                Assert.Equal(200, duplicate.StatusCode);
                Assert.Equal(1, provider!.OperationCount);

                // A later reconciliation must observe Succeeded and skip.
                var reconcile = await sender.Send(new ReconcileContributionCommand(contributionId));
                Assert.Contains("skipping", reconcile.Resolution, StringComparison.OrdinalIgnoreCase);
            }

            using (var db = CreateDbContext(fixture.PgConnectionString))
            {
                var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
                Assert.Equal(ContributionState.Succeeded, contribution.State);
                Assert.Equal(1, provider!.OperationCount);

                var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                    .Where(r => r.ContributionId == contributionId).ToListAsync();
                // With reconciliation disabled, the lost-response reference is never
                // revealed, so no local ProviderReference is persisted yet.
                Assert.Empty(refs);

                var callbackInboxes = await db.Set<InboxMessage>().IgnoreQueryFilters()
                    .Where(m => m.MessageId == "callback-e2e-callback-before-recon").ToListAsync();
                Assert.Single(callbackInboxes);

                var callbackTransitions = await db.Set<StateTransition>().IgnoreQueryFilters()
                    .Where(t => t.ContributionId == contributionId
                        && t.FromState == ContributionState.ReconciliationPending
                        && t.ToState == ContributionState.Succeeded
                        && t.Reason.Contains("Callback"))
                    .ToListAsync();
                Assert.Single(callbackTransitions);
            }
        }
        finally
        {
            // The fixture is disposed via await using; nothing else to stop.
        }
    }
}
