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
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using System.Text.Json;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
public class ProviderIdempotencyTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public ProviderIdempotencyTests(PostgreSqlFixture fixture)
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

    private async Task<(Guid orgId, Guid campaignId, Guid contributionId)> SeedDataAsync()
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

        return (orgId, campaignId, contributionId);
    }

    [Fact]
    public async Task SameContribution_ShouldProduceSingleProviderOperation()
    {
        var (orgId, campaignId, contributionId) = await SeedDataAsync();
        var sp = BuildServices("Success");

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test-correlation");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();

        var result1 = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "TEST-001"));
        var result2 = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "TEST-001"));

        Assert.Equal(Reliant.Domain.Enums.AttemptStatus.Succeeded, result1.Status);
        Assert.Equal(Reliant.Domain.Enums.AttemptStatus.Succeeded, result2.Status);
        Assert.Equal(result1.ProviderReference, result2.ProviderReference);

        var provider = scope.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        Assert.Equal(1, provider.OperationCount);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task AttemptPersisted_BeforeProviderCall()
    {
        var (orgId, campaignId, contributionId) = await SeedDataAsync();
        var sp = BuildServices("Success");

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test-correlation");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "TEST-001"));

        Assert.Equal(Reliant.Domain.Enums.AttemptStatus.Succeeded, result.Status);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var attempts = await db.Set<Reliant.Domain.Entities.ProcessingAttempt>()
            .IgnoreQueryFilters()
            .Where(a => a.ContributionId == contributionId)
            .ToListAsync();

        Assert.NotEmpty(attempts);
        Assert.Equal(Reliant.Domain.Enums.AttemptStatus.Succeeded, attempts[0].Status);
        Assert.NotEmpty(attempts[0].ProviderIdempotencyKey);
        Assert.Contains("sandbox", attempts[0].ProviderIdempotencyKey);

        TenantFilterAccessor.Clear();
    }
}
