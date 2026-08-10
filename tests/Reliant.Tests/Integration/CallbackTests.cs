using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Tenancy;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
public class CallbackTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public CallbackTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private IServiceProvider BuildServices(string providerMode = "Success")
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(AssemblyMarker).Assembly));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IContributionRepository, ContributionRepository>();
        services.AddScoped<ICampaignRepository, CampaignRepository>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();
        services.AddScoped<IStateTransitionRepository, StateTransitionRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IProcessingAttemptRepository, ProcessingAttemptRepository>();
        services.AddScoped<IProviderReferenceRepository, ProviderReferenceRepository>();
        services.AddScoped<IReconciliationRepository, ReconciliationRepository>();
        services.AddScoped<IOrphanProviderCallbackRepository, OrphanProviderCallbackRepository>();
        services.AddSingleton<IProviderOperationKeyFactory, ProviderOperationKeyFactory>();

        var configDict = new Dictionary<string, string?> { ["Provider:Mode"] = providerMode, ["Provider:Secret"] = "test-secret" };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        services.AddSingleton<IProvider, SandboxProvider>();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<Reliant.Application.Messaging.CircuitBreaker>();
        services.AddDbContext<ReliantDbContext>(opt => opt.UseNpgsql(_fixture.ConnectionString));

        return services.BuildServiceProvider();
    }

    private async Task<(Guid orgId, Guid contributionId)> SeedDataAsync(ContributionState state = ContributionState.Processing)
    {
        await _fixture.ResetAsync();
        var db = _fixture.DbContext;

        var orgId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();

        db.Set<Organization>().Add(new Organization
        {
            Id = orgId,
            Name = "Test Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Set<Campaign>().Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = orgId,
            Name = "Test",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Set<Contribution>().Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = orgId,
            CampaignId = campaignId,
            ExternalReference = "TEST-001",
            Amount = 100m,
            Currency = "USD",
            State = state,
            Version = 0
        });

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();
        TenantFilterAccessor.Clear();

        return (orgId, contributionId);
    }

    private static ProviderCallbackPayload Callback(
        string eventId,
        string status = "succeeded",
        string? providerReference = null,
        string? idempotencyKey = null)
    {
        return new ProviderCallbackPayload(
            EventId: eventId,
            EventType: "contribution.submit",
            ProviderReference: providerReference,
            IdempotencyKey: idempotencyKey,
            Status: status,
            OccurredAt: DateTime.UtcNow.ToString("O"),
            Version: 1);
    }

    [Fact]
    public async Task CallbackByProviderReference_ShouldLocateContribution()
    {
        var (orgId, contributionId) = await SeedDataAsync(ContributionState.Processing);
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        db.Set<ProviderReference>().Add(new ProviderReference
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = orgId,
            Reference = "ref_callback_001",
            ProviderName = "sandbox"
        });
        await db.SaveChangesAsync();

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new HandleProviderCallbackCommand(
            Callback("evt-001", "succeeded", providerReference: "ref_callback_001")));

        Assert.Equal(200, result.StatusCode);

        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Succeeded, contribution.State);

        var inbox = await db.Set<InboxMessage>().IgnoreQueryFilters().SingleAsync(x => x.MessageId == "callback-evt-001");
        Assert.Equal(InboxStatus.Processed, inbox.Status);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task CallbackByIdempotencyKey_ShouldLocateContribution()
    {
        var (orgId, contributionId) = await SeedDataAsync(ContributionState.Processing);
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        db.Set<ProcessingAttempt>().Add(new ProcessingAttempt
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = orgId,
            AttemptNumber = 1,
            ProviderIdempotencyKey = "reliant:sandbox:contribution:key-123:submit:v1",
            Status = AttemptStatus.Unknown,
            RequestPayload = "{}",
            StartedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new HandleProviderCallbackCommand(
            Callback("evt-002", "succeeded", idempotencyKey: "reliant:sandbox:contribution:key-123:submit:v1")));

        Assert.Equal(200, result.StatusCode);

        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Succeeded, contribution.State);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task OrphanCallback_ShouldBePersisted()
    {
        await _fixture.ResetAsync();
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new HandleProviderCallbackCommand(
            Callback("evt-orphan-1", "succeeded", providerReference: "ref_unknown_999")));

        Assert.Equal(200, result.StatusCode);
        Assert.Contains("Orphan", result.Message);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var orphan = await db.Set<OrphanProviderCallback>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.EventId == "evt-orphan-1");

        Assert.NotNull(orphan);
        Assert.Equal("sandbox", orphan!.ProviderName);
        Assert.Equal("ref_unknown_999", orphan.ProviderReference);
        Assert.False(orphan.Resolved);
    }

    [Fact]
    public async Task TerminalStateConflict_ShouldCreateManualRequiredReconciliation()
    {
        var (orgId, contributionId) = await SeedDataAsync(ContributionState.Succeeded);
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        db.Set<ProviderReference>().Add(new ProviderReference
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = orgId,
            Reference = "ref_conflict_001",
            ProviderName = "sandbox"
        });
        await db.SaveChangesAsync();

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new HandleProviderCallbackCommand(
            Callback("evt-conflict-1", "failed", providerReference: "ref_conflict_001")));

        Assert.Equal(200, result.StatusCode);
        Assert.Contains("ManualRequired", result.Message);

        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Succeeded, contribution.State);

        var record = await db.Set<ReconciliationRecord>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(r => r.ContributionId == contributionId);
        Assert.NotNull(record);
        Assert.Equal("ManualRequired", record!.Resolution);

        var alert = await db.Set<OutboxMessage>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(o => o.MessageType == "OperatorAlert");
        Assert.NotNull(alert);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task UnknownCallbackStatus_ShouldReturn400_WithoutProcessedInbox()
    {
        await _fixture.ResetAsync();
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new HandleProviderCallbackCommand(
            Callback("evt-unknown-1", "bogus_status", providerReference: "ref_unknown_001")));

        Assert.Equal(400, result.StatusCode);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var inbox = await db.Set<InboxMessage>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.MessageId == "callback-evt-unknown-1");
        Assert.Null(inbox);
    }

    [Fact]
    public async Task CallbackDuringReconciliationPending_ShouldConvergeToSucceeded()
    {
        var (orgId, contributionId) = await SeedDataAsync(ContributionState.ReconciliationPending);
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        db.Set<ProviderReference>().Add(new ProviderReference
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = orgId,
            Reference = "ref_recon_pending_001",
            ProviderName = "sandbox"
        });
        await db.SaveChangesAsync();

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new HandleProviderCallbackCommand(
            Callback("evt-recon-1", "succeeded", providerReference: "ref_recon_pending_001")));

        Assert.Equal(200, result.StatusCode);

        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Succeeded, contribution.State);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task CallbackSucceeded_WhenAlreadySucceeded_ShouldNotAddTransition()
    {
        var (orgId, contributionId) = await SeedDataAsync(ContributionState.Succeeded);
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        db.Set<ProviderReference>().Add(new ProviderReference
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = orgId,
            Reference = "ref_already_001",
            ProviderName = "sandbox"
        });
        await db.SaveChangesAsync();

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new HandleProviderCallbackCommand(
            Callback("evt-already-1", "succeeded", providerReference: "ref_already_001")));

        Assert.Equal(200, result.StatusCode);

        var transitions = await db.Set<StateTransition>().IgnoreQueryFilters()
            .Where(t => t.ContributionId == contributionId)
            .ToListAsync();
        Assert.Empty(transitions);

        TenantFilterAccessor.Clear();
    }
}
