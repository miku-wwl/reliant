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
public class FinalE2ETests : IClassFixture<WorkerHostFixture>
{
    private readonly WorkerHostFixture _fixture;

    public FinalE2ETests(WorkerHostFixture fixture)
    {
        _fixture = fixture;
    }

    private ReliantDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(_fixture.PgConnectionString)
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

    [Fact]
    public async Task ProcessedResponseLost_WithDuplicateMessageAndCallback_ShouldConverge_WithoutSecondProviderEffect()
    {
        // Provider processes the operation but its response is lost -> Unknown ->
        // reconciliation must converge to Succeeded with exactly one provider effect.
        await _fixture.StartWorkersAsync(providerMode: "ProcessedButResponseLost");
        try
        {
            Guid orgId;
            Guid contributionId;
            using (var db = CreateDbContext())
            {
                (orgId, contributionId) = await SeedCreatedContributionWithOutboxAsync(db);
            }

            var provider = _fixture.Host.Services.GetRequiredService<IProvider>() as SandboxProvider;
            Assert.NotNull(provider);

            // 1) Real pipeline: Outbox -> SQS -> Worker -> Provider -> Reconciliation.
            var converged = await WaitUntilAsync(async () =>
            {
                using var db = CreateDbContext();
                var c = await db.Set<Contribution>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == contributionId);
                return c?.State == ContributionState.Succeeded;
            }, TimeSpan.FromSeconds(45));

            Assert.True(converged, "Contribution did not converge to Succeeded within timeout");

            using (var db = CreateDbContext())
            {
                var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
                Assert.Equal(ContributionState.Succeeded, contribution.State);

                // Exactly one provider-side operation.
                Assert.Equal(1, provider!.OperationCount);

                // Exactly one local provider reference.
                var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                    .Where(r => r.ContributionId == contributionId).ToListAsync();
                Assert.Single(refs);

                // The full state path was audited: Created->Processing (entry),
                // Processing->ProviderUnknown, ProviderUnknown->ReconciliationPending,
                // ReconciliationPending->Succeeded.
                var transitions = await db.Set<StateTransition>().IgnoreQueryFilters()
                    .Where(t => t.ContributionId == contributionId).ToListAsync();
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

            // 2) Duplicate callback: no second business effect, no new transition.
            using (var scope = _fixture.Host.Services.CreateScope())
            {
                var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
                var keyFactory = scope.ServiceProvider.GetRequiredService<IProviderOperationKeyFactory>();
                var idempotencyKey = keyFactory.CreateContributionSubmitKey(orgId, contributionId, "sandbox");

                var callback = await sender.Send(new HandleProviderCallbackCommand(new ProviderCallbackPayload(
                    EventId: "e2e-callback-1",
                    EventType: "contribution.submit",
                    ProviderReference: null,
                    IdempotencyKey: idempotencyKey,
                    Status: "succeeded",
                    OccurredAt: DateTime.UtcNow.ToString("O"),
                    Version: 1)));

                Assert.Equal(200, callback.StatusCode);
                Assert.Equal(1, provider!.OperationCount);
            }

            // 3) Duplicate message redelivery: inbox dedup prevents reprocessing.
            using (var db = CreateDbContext())
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

            using (var db = CreateDbContext())
            {
                var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
                    .Where(r => r.ContributionId == contributionId).ToListAsync();
                Assert.Single(refs);
            }
        }
        finally
        {
            await _fixture.StopWorkersAsync();
        }
    }
}
