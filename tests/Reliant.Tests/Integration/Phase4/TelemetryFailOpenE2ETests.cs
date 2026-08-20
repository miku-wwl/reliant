using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Observability;
using Reliant.Application.Tenancy;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;

namespace Reliant.Tests.Integration.Phase4;

[Trait("Category", "Phase4")]
[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class TelemetryFailOpenE2ETests
{
    [Fact]
    public async Task UnreachableCollector_ShouldNotBlockCommitProcessingOrAck()
    {
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        await SeedAsync(
            fixture.PgConnectionString,
            organizationId,
            campaignId);

        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeReconciliation: false,
            processingConcurrency: 1,
            configurationOverrides:
                new Dictionary<string, string?>
                {
                    ["Telemetry:OtlpEndpoint"] =
                        "http://127.0.0.1:1",
                    ["Telemetry:OtlpProtocol"] = "grpc"
                });

        using var root = ReliantTelemetry.StartActivity(
            "phase4 collector unavailable");
        var created = await CreateContributionAsync(
            fixture,
            organizationId,
            campaignId);
        Assert.NotNull(created.Body);
        var contributionId = created.Body!.Id;

        var completed = await WaitUntilAsync(
            async () =>
            {
                await using var database = CreateDbContext(
                    fixture.PgConnectionString);
                var contribution = await database.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                return contribution.State == ContributionState.Succeeded;
            },
            TimeSpan.FromSeconds(60));
        Assert.True(
            completed,
            fixture.RecentLogs(100));

        await using var finalDatabase = CreateDbContext(
            fixture.PgConnectionString);
        var outbox = await finalDatabase.OutboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Payload.Contains(
                contributionId.ToString()));
        var inbox = await finalDatabase.InboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(x =>
                x.MessageId == outbox.Id.ToString());
        var provider = Assert.IsType<SandboxProvider>(
            fixture.Host.Services.GetRequiredService<IProvider>());

        Assert.Equal(OutboxStatus.Sent, outbox.Status);
        Assert.False(string.IsNullOrWhiteSpace(outbox.TraceParent));
        Assert.Equal(InboxStatus.Processed, inbox.Status);
        Assert.Equal(1, provider.OperationCount);
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
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .SetTenant(
                organizationId,
                null,
                null,
                "phase4-fail-open");
        TenantFilterAccessor.SetOrganizationId(organizationId);
        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<MediatR.ISender>();
            return await sender.Send(
                new CreateContributionCommand(
                    campaignId,
                    "PHASE4-FAIL-OPEN",
                    42m,
                    "NZD",
                    $"phase4-{Guid.NewGuid():N}"));
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private static async Task SeedAsync(
        string connectionString,
        Guid organizationId,
        Guid campaignId)
    {
        await using var database = CreateDbContext(connectionString);
        database.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 4 Fail-open Org",
            Status = OrganizationStatus.Active
        });
        database.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 4 Fail-open Campaign",
            Status = CampaignStatus.Active
        });
        await database.SaveChangesAsync();
    }

    private static ReliantDbContext CreateDbContext(
        string connectionString)
        => new(
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(connectionString)
                .Options);

    private static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(250);
        }

        return await condition();
    }
}
