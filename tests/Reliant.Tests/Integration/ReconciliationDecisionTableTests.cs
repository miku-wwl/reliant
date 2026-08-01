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
public class ReconciliationDecisionTableTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public ReconciliationDecisionTableTests(PostgreSqlFixture fixture)
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
        services.AddSingleton(TimeProvider.System);

        var configDict = new Dictionary<string, string?> { ["Provider:Mode"] = providerMode, ["Provider:Secret"] = "test-secret" };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        services.AddSingleton<IProvider, SandboxProvider>();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<Reliant.Application.Messaging.CircuitBreaker>();
        services.AddDbContext<ReliantDbContext>(opt => opt.UseNpgsql(_fixture.ConnectionString));

        return services.BuildServiceProvider();
    }

    private async Task<(Guid orgId, Guid contributionId)> SeedReconciliationPendingAsync(
        string? attemptKey = null, string? reference = null, int reconciliationRecords = 0)
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
            ExternalReference = "RECON-001",
            Amount = 100m,
            Currency = "USD",
            State = ContributionState.ReconciliationPending,
            Version = 0
        });

        if (attemptKey is not null)
        {
            db.Set<ProcessingAttempt>().Add(new ProcessingAttempt
            {
                Id = Guid.NewGuid(),
                ContributionId = contributionId,
                OrganizationId = orgId,
                AttemptNumber = 1,
                ProviderName = "sandbox",
                ProviderIdempotencyKey = attemptKey,
                Status = AttemptStatus.Unknown,
                RequestPayload = "{}"
            });
        }

        if (reference is not null)
        {
            db.Set<ProviderReference>().Add(new ProviderReference
            {
                Id = Guid.NewGuid(),
                ContributionId = contributionId,
                OrganizationId = orgId,
                Reference = reference,
                ProviderName = "sandbox"
            });
        }

        for (var i = 0; i < reconciliationRecords; i++)
        {
            db.Set<ReconciliationRecord>().Add(new ReconciliationRecord
            {
                Id = Guid.NewGuid(),
                ContributionId = contributionId,
                OrganizationId = orgId,
                LocalState = ContributionState.ReconciliationPending,
                ProviderState = "Pending",
                Difference = ReconciliationDifference.None,
                Resolution = "WaitNextCycle"
            });
        }

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();
        TenantFilterAccessor.Clear();

        return (orgId, contributionId);
    }

    private static void SetTenant(IServiceProvider sp, Guid orgId)
    {
        var tenantContext = sp.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);
    }

    private static async Task<SandboxProvider> CreateProviderOperationAsync(
        IServiceProvider sp, string key, decimal amount = 100m, string currency = "USD")
    {
        var provider = sp.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        await provider!.SubmitAsync(new ProviderRequest(key, amount, currency, "RECON-001"));
        return provider;
    }

    [Fact]
    public async Task ProviderSucceeded_ShouldConvergeToSucceeded()
    {
        const string key = "recon-key-succeeded";
        var (orgId, contributionId) = await SeedReconciliationPendingAsync(attemptKey: key);
        var sp = BuildServices("Success");

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);
        var provider = await CreateProviderOperationAsync(scope.ServiceProvider, key);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        TenantFilterAccessor.SetOrganizationId(orgId);
        var result = await sender.Send(new ReconcileContributionCommand(contributionId));

        Assert.True(result.Resolved, $"Resolution=[{result.Resolution}] Difference=[{result.Difference}]");
        Assert.Equal("AutoFixed", result.Resolution);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Succeeded, contribution.State);

        var record = await db.Set<ReconciliationRecord>().IgnoreQueryFilters().SingleAsync(r => r.ContributionId == contributionId);
        Assert.NotNull(record.ResolvedAt);
        Assert.Equal("AutoFixed", record.Resolution);

        var transition = await db.Set<StateTransition>().IgnoreQueryFilters().SingleAsync(t => t.ContributionId == contributionId);
        Assert.Equal(ContributionState.ReconciliationPending, transition.FromState);
        Assert.Equal(ContributionState.Succeeded, transition.ToState);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task ProviderFailed_ShouldConvergeToFailed()
    {
        const string key = "recon-key-failed";
        var (orgId, contributionId) = await SeedReconciliationPendingAsync(attemptKey: key);
        var sp = BuildServices("Success");

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);
        var provider = await CreateProviderOperationAsync(scope.ServiceProvider, key);
        // Cancel the operation so the provider reports a definitive failure.
        var query = await provider.QueryStatusByIdempotencyKeyAsync(key);
        Assert.NotNull(query.ProviderReference);
        await provider.CancelAsync(query.ProviderReference!);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new ReconcileContributionCommand(contributionId));

        Assert.True(result.Resolved);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Failed, contribution.State);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task ProviderPending_ShouldRemainReconciliationPending()
    {
        const string key = "recon-key-pending";
        var (orgId, contributionId) = await SeedReconciliationPendingAsync(attemptKey: key);
        // PendingForever: the operation stays Pending and never advances.
        var sp = BuildServices("PendingForever");

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);
        await CreateProviderOperationAsync(scope.ServiceProvider, key); // op stays Pending on first query

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new ReconcileContributionCommand(contributionId));

        Assert.False(result.Resolved);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.ReconciliationPending, contribution.State);

        var record = await db.Set<ReconciliationRecord>().IgnoreQueryFilters().SingleAsync(r => r.ContributionId == contributionId);
        Assert.Null(record.ResolvedAt);
        Assert.Equal("WaitNextCycle", record.Resolution);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task ProviderNotFound_ShouldSetRetryPendingAndNextRetryAt()
    {
        const string key = "recon-key-notfound";
        var (orgId, contributionId) = await SeedReconciliationPendingAsync(attemptKey: key);
        // TimeoutBeforeProcessing never creates a provider operation -> NotFound.
        var sp = BuildServices("TimeoutBeforeProcessing");

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new ReconcileContributionCommand(contributionId));

        Assert.True(result.Resolved);
        Assert.Equal("SafeRetry", result.Resolution);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.RetryPending, contribution.State);
        // A real retry schedule must be set so the scheduler can pick it up.
        Assert.NotNull(contribution.NextRetryAt);
        Assert.True(contribution.NextRetryAt > DateTime.UtcNow);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task ProviderUnavailable_ShouldRemainPendingAndUnresolved()
    {
        const string key = "recon-key-unavailable";
        var (orgId, contributionId) = await SeedReconciliationPendingAsync(attemptKey: key);
        var sp = BuildServices("QueryUnavailable");

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);
        await CreateProviderOperationAsync(scope.ServiceProvider, key); // op exists but queries throw

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new ReconcileContributionCommand(contributionId));

        Assert.False(result.Resolved);
        Assert.Contains("unavailable", result.Resolution, StringComparison.OrdinalIgnoreCase);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.ReconciliationPending, contribution.State);

        var record = await db.Set<ReconciliationRecord>().IgnoreQueryFilters().SingleAsync(r => r.ContributionId == contributionId);
        Assert.Equal("Unavailable", record.ProviderState);
        Assert.Equal(ReconciliationDifference.ProviderUnavailable, record.Difference);
        Assert.Null(record.ResolvedAt);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task MissingLocalAttempt_ShouldCreateManualRequired()
    {
        var (orgId, contributionId) = await SeedReconciliationPendingAsync();
        var sp = BuildServices("Success");

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new ReconcileContributionCommand(contributionId));

        Assert.False(result.Resolved);
        Assert.Contains("ManualRequired", result.Resolution);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var record = await db.Set<ReconciliationRecord>().IgnoreQueryFilters().SingleAsync(r => r.ContributionId == contributionId);
        Assert.Equal("ManualRequired", record.Resolution);
        Assert.Null(record.ResolvedAt);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task MaxReconciliationCount_ShouldCreateManualRequiredAlert()
    {
        var (orgId, contributionId) = await SeedReconciliationPendingAsync(reconciliationRecords: 20);
        var sp = BuildServices("Success");

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new ReconcileContributionCommand(contributionId));

        // ManualRequired is NOT resolved: an operator must act on the contribution.
        Assert.False(result.Resolved);
        Assert.Contains("ManualRequired", result.Resolution);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Failed, contribution.State);

        var alert = await db.Set<OutboxMessage>().IgnoreQueryFilters()
            .Where(o => o.MessageType == "OperatorAlert" && o.OrganizationId == orgId)
            .ToListAsync();
        Assert.Single(alert);

        TenantFilterAccessor.Clear();
    }
}
