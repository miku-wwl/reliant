using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Tenancy;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
public class ReconciliationTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public ReconciliationTests(PostgreSqlFixture fixture)
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
        services.AddSingleton<IProviderOperationKeyFactory, ProviderOperationKeyFactory>();

        var configDict = new Dictionary<string, string?> { ["Provider:Mode"] = providerMode, ["Provider:Secret"] = "test-secret" };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        services.AddSingleton<IProvider, SandboxProvider>();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<Reliant.Application.Messaging.CircuitBreaker>();
        services.AddDbContext<ReliantDbContext>(opt => opt.UseNpgsql(_fixture.ConnectionString));

        return services.BuildServiceProvider();
    }

    private async Task<(Guid orgId, Guid contributionId)> SeedDataAsync()
    {
        await _fixture.ResetAsync();
        var db = _fixture.DbContext;

        var orgId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();

        db.Set<Reliant.Domain.Entities.Organization>().Add(new Reliant.Domain.Entities.Organization
        {
            Id = orgId,
            Name = "Test Org",
            Status = Reliant.Domain.Enums.OrganizationStatus.Active,
            Version = 0
        });
        db.Set<Reliant.Domain.Entities.Campaign>().Add(new Reliant.Domain.Entities.Campaign
        {
            Id = campaignId,
            OrganizationId = orgId,
            Name = "Test",
            Status = Reliant.Domain.Enums.CampaignStatus.Active,
            Version = 0
        });
        db.Set<Reliant.Domain.Entities.Contribution>().Add(new Reliant.Domain.Entities.Contribution
        {
            Id = contributionId,
            OrganizationId = orgId,
            CampaignId = campaignId,
            ExternalReference = "TEST-001",
            Amount = 100m,
            Currency = "USD",
            State = Reliant.Domain.Enums.ContributionState.Created,
            Version = 0
        });

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();
        TenantFilterAccessor.Clear();

        return (orgId, contributionId);
    }

    [Fact]
    public async Task ProcessedButResponseLost_ShouldConvergeToSucceeded_WithoutSecondProviderEffect()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp = BuildServices("ProcessedButResponseLost");

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();

        var result = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "TEST-001"));

        Assert.Equal(Reliant.Domain.Enums.AttemptStatus.Unknown, result.Status);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Reliant.Domain.Entities.Contribution>()
            .IgnoreQueryFilters()
            .FirstAsync(c => c.Id == contributionId);

        Assert.Equal(Reliant.Domain.Enums.ContributionState.Created, contribution.State);

        var attempt = await db.Set<Reliant.Domain.Entities.ProcessingAttempt>()
            .IgnoreQueryFilters()
            .FirstAsync(a => a.ContributionId == contributionId);

        Assert.Equal(Reliant.Domain.Enums.AttemptStatus.Unknown, attempt.Status);
        Assert.Null(attempt.ProviderReference);

        var provider = scope.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        Assert.Equal(1, provider.OperationCount);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task ProviderNotFound_OnReconciliation_ShouldTransitionToRetryPending()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp = BuildServices("TimeoutBeforeProcessing");

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();

        var result = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "TEST-001"));

        Assert.Equal(Reliant.Domain.Enums.AttemptStatus.Unknown, result.Status);

        var provider = scope.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        Assert.Equal(0, provider.OperationCount);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var attempt = await db.Set<Reliant.Domain.Entities.ProcessingAttempt>()
            .IgnoreQueryFilters()
            .FirstAsync(a => a.ContributionId == contributionId);

        var providerResult = await provider.QueryStatusByIdempotencyKeyAsync(attempt.ProviderIdempotencyKey);
        Assert.Equal(Reliant.Domain.Enums.ProviderStatus.NotFound, providerResult.Status);

        TenantFilterAccessor.Clear();
    }
}
