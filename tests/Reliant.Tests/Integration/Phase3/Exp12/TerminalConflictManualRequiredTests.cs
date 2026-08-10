using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Tenancy;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Tests.Integration.Fixtures;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp12;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
public sealed class TerminalConflictManualRequiredTests :
    IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;

    public TerminalConflictManualRequiredTests(
        PostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task OppositeTerminalCallbacks_ShouldNotOverwriteLocalState()
    {
        var startedAt = DateTime.UtcNow;

        await VerifyConflictAsync(
            localState: ContributionState.Failed,
            callbackStatus: "succeeded",
            expectedProviderState: "Succeeded",
            eventId: "phase3-exp12-failed-vs-succeeded");
        await VerifyConflictAsync(
            localState: ContributionState.Succeeded,
            callbackStatus: "failed",
            expectedProviderState: "Failed",
            eventId: "phase3-exp12-succeeded-vs-failed");

        _output.WriteLine(
            "FINAL | Scenarios=Failed-vs-Succeeded," +
            "Succeeded-vs-Failed | LocalOverwrite=0 | " +
            "ManualRequired=2/2 | OperatorAlert=2/2 | " +
            "RESULT=PASS | StartedAt={0:O} | CompletedAt={1:O}",
            startedAt,
            DateTime.UtcNow);
    }

    private async Task VerifyConflictAsync(
        ContributionState localState,
        string callbackStatus,
        string expectedProviderState,
        string eventId)
    {
        var (organizationId, contributionId, providerReference) =
            await SeedTerminalContributionAsync(
                localState,
                eventId);
        using var services = BuildServices();

        CallbackHandleResult result;
        using (var scope = services.CreateScope())
        {
            var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantContext>();
            tenant.SetTenant(
                organizationId,
                userId: null,
                role: null,
                correlationId: eventId);
            TenantFilterAccessor.SetOrganizationId(organizationId);
            try
            {
                var sender = scope.ServiceProvider
                    .GetRequiredService<MediatR.ISender>();
                result = await sender.Send(
                    new HandleProviderCallbackCommand(
                        new ProviderCallbackPayload(
                            EventId: eventId,
                            EventType: "contribution.submit",
                            ProviderReference: providerReference,
                            IdempotencyKey: null,
                            Status: callbackStatus,
                            OccurredAt: DateTime.UtcNow.ToString("O"),
                            Version: 1)));
            }
            finally
            {
                TenantFilterAccessor.Clear();
            }
        }

        Assert.Equal(200, result.StatusCode);
        Assert.Contains("ManualRequired", result.Message);

        await using var db = CreateDbContext();
        var contribution = await db.Contributions
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == contributionId);
        var record = await db.ReconciliationRecords
            .IgnoreQueryFilters()
            .SingleAsync(x => x.ContributionId == contributionId);
        var alert = await db.OutboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(x =>
                x.OrganizationId == organizationId &&
                x.MessageType == "OperatorAlert");
        var inbox = await db.InboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(x =>
                x.MessageId == $"callback-{eventId}");

        Assert.Equal(localState, contribution.State);
        Assert.Equal(0, contribution.Version);
        Assert.Equal(localState, record.LocalState);
        Assert.Equal(expectedProviderState, record.ProviderState);
        Assert.Equal(
            ReconciliationDifference.StateMismatch,
            record.Difference);
        Assert.Equal("ManualRequired", record.Resolution);
        Assert.Null(record.ResolvedAt);
        Assert.Null(record.ResolvedBy);
        Assert.Equal(OutboxStatus.Pending, alert.Status);
        Assert.Equal(0, alert.SendCount);
        Assert.False(string.IsNullOrWhiteSpace(alert.CorrelationId));
        Assert.Equal(InboxStatus.Processed, inbox.Status);
        Assert.Equal("ProviderCallback", inbox.MessageType);
        Assert.Equal("CallbackHandler", inbox.HandlerName);
        Assert.Equal(organizationId, inbox.OrganizationId);

        using (var payload = JsonDocument.Parse(alert.Payload))
        {
            var root = payload.RootElement;
            Assert.Equal(
                "Callback conflicts with local terminal state",
                root.GetProperty("alert").GetString());
            Assert.Equal(
                contributionId,
                root.GetProperty("contributionId").GetGuid());
            Assert.Equal(
                localState.ToString(),
                root.GetProperty("localState").GetString());
            Assert.Equal(
                expectedProviderState,
                root.GetProperty("providerState").GetString());
        }

        Assert.Equal(
            0,
            await db.StateTransitions
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.ContributionId == contributionId));
        Assert.Equal(
            0,
            await db.AuditEvents
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.EntityId == contributionId));
        Assert.Equal(
            0,
            await db.OrphanProviderCallbacks
                .IgnoreQueryFilters()
                .CountAsync(x => x.EventId == eventId));
        Assert.Equal(
            1,
            await db.ProviderReferences
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.ContributionId == contributionId));
        Assert.Equal(
            1,
            await db.ReconciliationRecords
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.ContributionId == contributionId));
        Assert.Equal(
            1,
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.OrganizationId == organizationId));
        Assert.Equal(
            1,
            await db.InboxMessages
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.MessageId == $"callback-{eventId}"));

        _output.WriteLine(
            "CONFLICT | Local={0} | Provider={1} | HTTP-equivalent=200 | " +
            "LocalVersion=0 | StateTransition=0 | " +
            "Reconciliation=1/ManualRequired | " +
            "OperatorAlert=1/Pending | Inbox=1 | AuditEvent=0 | " +
            "ConflictPayload=complete",
            localState,
            expectedProviderState);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddMediatR(config =>
            config.RegisterServicesFromAssembly(
                typeof(AssemblyMarker).Assembly));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IContributionRepository,
            ContributionRepository>();
        services.AddScoped<IProviderReferenceRepository,
            ProviderReferenceRepository>();
        services.AddScoped<IProcessingAttemptRepository,
            ProcessingAttemptRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IStateTransitionRepository,
            StateTransitionRepository>();
        services.AddScoped<IReconciliationRepository,
            ReconciliationRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IOrphanProviderCallbackRepository,
            OrphanProviderCallbackRepository>();
        services.AddDbContext<ReliantDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        return services.BuildServiceProvider();
    }

    private async Task<(
        Guid OrganizationId,
        Guid ContributionId,
        string ProviderReference)>
        SeedTerminalContributionAsync(
            ContributionState state,
            string suffix)
    {
        await _fixture.ResetAsync();
        var db = _fixture.DbContext;
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var providerReference =
            $"ref_exp12_{suffix.GetHashCode():x}";

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 3 Experiment 12 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 12",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Contributions.Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = organizationId,
            CampaignId = campaignId,
            ExternalReference = $"PHASE3-EXP12-{state}",
            Amount = 300m,
            Currency = "NZD",
            State = state,
            Version = 0
        });
        db.ProviderReferences.Add(new ProviderReference
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = organizationId,
            Reference = providerReference,
            ProviderName = "sandbox"
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (
            organizationId,
            contributionId,
            providerReference);
    }

    private ReliantDbContext CreateDbContext()
    {
        var options =
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(_fixture.ConnectionString)
                .Options;
        return new ReliantDbContext(options);
    }
}
