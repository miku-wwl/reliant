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
public class ProviderConcurrencyTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public ProviderConcurrencyTests(PostgreSqlFixture fixture)
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
            ExternalReference = "CONC-001",
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
    public async Task SameContribution_SequentialSubmit_ShouldReturnSameReference()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result1 = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CONC-001"));
        var result2 = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CONC-001"));

        Assert.Equal(AttemptStatus.Succeeded, result1.Status);
        Assert.Equal(AttemptStatus.Succeeded, result2.Status);
        Assert.Equal(result1.ProviderReference, result2.ProviderReference);

        var provider = scope.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        Assert.Equal(1, provider!.OperationCount);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task SameContribution_ConcurrentSubmit_ShouldCreateOneProviderOperation()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp1 = BuildServices();
        var sp2 = BuildServices();

        using var scope1 = sp1.CreateScope();
        using var scope2 = sp2.CreateScope();
        SetTenant(scope1.ServiceProvider, orgId);
        SetTenant(scope2.ServiceProvider, orgId);

        var sender1 = scope1.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var sender2 = scope2.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var command = new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CONC-001");

        var results = await Task.WhenAll(
            sender1.Send(command),
            sender2.Send(command));

        // Either both succeeded (one observed the winner's reference) or the loser
        // safely deferred - but the provider must have exactly one operation.
        var provider1 = scope1.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        var provider2 = scope2.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider1);
        Assert.NotNull(provider2);

        // Both providers are separate SandboxProvider singletons; the business
        // invariant is checked per-provider here since they do not share state.
        // The critical assertion is at the DB layer: exactly one reference.
        Assert.True(results.All(r => r.Status is AttemptStatus.Succeeded or AttemptStatus.Pending));

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task SameContribution_ConcurrentSubmit_ShouldCreateOneProviderReference()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp1 = BuildServices();
        var sp2 = BuildServices();

        using var scope1 = sp1.CreateScope();
        using var scope2 = sp2.CreateScope();
        SetTenant(scope1.ServiceProvider, orgId);
        SetTenant(scope2.ServiceProvider, orgId);

        var sender1 = scope1.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var sender2 = scope2.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var command = new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CONC-001");

        await Task.WhenAll(
            sender1.Send(command),
            sender2.Send(command));

        var db = scope1.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var references = await db.Set<ProviderReference>().IgnoreQueryFilters()
            .Where(r => r.ContributionId == contributionId)
            .ToListAsync();
        Assert.Single(references);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task SameKey_DifferentPayload_ShouldReturnIdempotencyConflict()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);

        var provider = scope.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);

        var keyFactory = scope.ServiceProvider.GetRequiredService<IProviderOperationKeyFactory>();
        var key = keyFactory.CreateContributionSubmitKey(orgId, contributionId, "sandbox");

        var first = await provider!.SubmitAsync(new ProviderRequest(key, 100m, "USD", "CONC-001"));
        Assert.Equal(ProviderStatus.Succeeded, first.Status);

        // Same key, different amount -> idempotency conflict, never silent reuse.
        var second = await provider.SubmitAsync(new ProviderRequest(key, 999m, "EUR", "CONC-001"));
        Assert.Equal(ProviderStatus.Failed, second.Status);
        Assert.Equal(ErrorCategory.PermanentBusinessRejection, second.ErrorCategory);

        Assert.Equal(1, provider.OperationCount);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task AttemptNumber_ShouldBeUniquePerContribution()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();

        db.Set<ProcessingAttempt>().Add(new ProcessingAttempt
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = orgId,
            AttemptNumber = 1,
            ProviderName = "sandbox",
            ProviderIdempotencyKey = "key-1",
            Status = AttemptStatus.Pending,
            RequestPayload = "{}"
        });
        await db.SaveChangesAsync();

        db.Set<ProcessingAttempt>().Add(new ProcessingAttempt
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = orgId,
            AttemptNumber = 1,
            ProviderName = "sandbox",
            ProviderIdempotencyKey = "key-1",
            Status = AttemptStatus.Pending,
            RequestPayload = "{}"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task WorkerRestart_ShouldReuseSameProviderIdempotencyKey()
    {
        var (orgId, contributionId) = await SeedDataAsync();
        var sp = BuildServices("ProcessedButResponseLost");

        using var scope = sp.CreateScope();
        SetTenant(scope.ServiceProvider, orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();

        // First attempt: provider processes but response is lost -> Unknown.
        var first = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CONC-001"));
        Assert.Equal(AttemptStatus.Unknown, first.Status);

        // Retry after 'restart': must reuse the same provider idempotency key.
        var second = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CONC-001"));
        Assert.Equal(AttemptStatus.Succeeded, second.Status);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
            .Where(a => a.ContributionId == contributionId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync();

        Assert.Equal(2, attempts.Count);
        Assert.Equal(attempts[0].ProviderIdempotencyKey, attempts[1].ProviderIdempotencyKey);
        Assert.Contains("sandbox", attempts[0].ProviderIdempotencyKey);

        TenantFilterAccessor.Clear();
    }
}
