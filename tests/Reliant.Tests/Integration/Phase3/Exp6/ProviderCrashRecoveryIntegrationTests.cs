using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Messaging;
using Reliant.Application.Tenancy;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;
using Reliant.Tests.TestHelpers;

namespace Reliant.Tests.Integration.Phase3.Exp6;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
public class ProviderCrashRecoveryIntegrationTests :
    IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public ProviderCrashRecoveryIntegrationTests(
        PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private IServiceProvider BuildServices(string providerMode = "Success", IWorkerFaultInjector? faultInjector = null)
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
        services.AddSingleton<IWorkerFaultInjector>(faultInjector ?? new NoopWorkerFaultInjector());

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
            ExternalReference = "CRASH-001",
            Amount = 100m,
            Currency = "USD",
            State = ContributionState.Processing,
            Version = 0
        });

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

    [Fact]
    public async Task CrashAfterAttemptPersisted_ShouldRecoverWithSameKey_WithoutSecondEffect()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        // One provider instance: crash once, then recover against the same one.
        var sp = BuildServices("Success", new ThrowingFaultInjector(WorkerFaultPoint.AfterAttemptPersisted));

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);
        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var command = new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CRASH-001");

        // Crash after the pending attempt was committed, before the provider call.
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send(command));

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
            .Where(a => a.ContributionId == contributionId)
            .ToListAsync();
        // The attempt evidence is durable even though the worker crashed.
        Assert.Single(attempts);
        Assert.Equal(AttemptStatus.Pending, attempts[0].Status);

        var provider = scope.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        Assert.Equal(0, provider!.OperationCount);

        // Recovery on the same provider: same key, exactly one provider effect.
        var recovered = await sender.Send(command);

        Assert.Equal(AttemptStatus.Succeeded, recovered.Status);
        Assert.Equal(1, provider.OperationCount);

        var keyFactory = scope.ServiceProvider.GetRequiredService<IProviderOperationKeyFactory>();
        var key = keyFactory.CreateContributionSubmitKey(orgId, contributionId, "sandbox");
        var allAttempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
            .Where(a => a.ContributionId == contributionId)
            .ToListAsync();
        Assert.All(allAttempts, a => Assert.Equal(key, a.ProviderIdempotencyKey));

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task CrashBeforeResponseHandled_ShouldNotCreateSecondProviderEffect()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp = BuildServices("Success", new ThrowingFaultInjector(WorkerFaultPoint.BeforeProviderResponseHandled));

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);
        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var command = new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CRASH-001");

        // Provider processed, but the worker 'crashed' before handling the response.
        var crashed = await sender.Send(command);
        Assert.Equal(AttemptStatus.Unknown, crashed.Status);

        var provider = scope.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        // The provider side processed exactly one operation.
        Assert.Equal(1, provider!.OperationCount);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var refs = await db.Set<ProviderReference>().IgnoreQueryFilters()
            .Where(r => r.ContributionId == contributionId)
            .ToListAsync();
        Assert.Empty(refs); // no local reference was persisted before the crash

        // Recovery on the same provider: same key -> original result, still 1 op.
        var recovered = await sender.Send(command);

        Assert.Equal(AttemptStatus.Succeeded, recovered.Status);
        Assert.Equal(1, provider.OperationCount);

        var refsAfter = await db.Set<ProviderReference>().IgnoreQueryFilters()
            .Where(r => r.ContributionId == contributionId)
            .ToListAsync();
        Assert.Single(refsAfter);

        TenantFilterAccessor.Clear();
    }

}
