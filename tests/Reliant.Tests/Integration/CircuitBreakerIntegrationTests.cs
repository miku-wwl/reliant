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

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
public class CircuitBreakerIntegrationTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public CircuitBreakerIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private IServiceProvider BuildServices(FakeTimeProvider? clock = null)
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

        var configDict = new Dictionary<string, string?> { ["Provider:Mode"] = "Success", ["Provider:Secret"] = "test-secret" };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        services.AddSingleton<IProvider, SandboxProvider>();
        services.AddSingleton<IConfiguration>(config);
        // Threshold 1 so a single failure opens the circuit; fake clock for determinism.
        services.AddSingleton(sp => new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 10, clock ?? TimeProvider.System));
        services.AddDbContext<ReliantDbContext>(opt => opt.UseNpgsql(_fixture.ConnectionString));

        return services.BuildServiceProvider();
    }

    private async Task<(Guid orgId, Guid contributionId)> SeedProcessingAsync()
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
            ExternalReference = "CB-001",
            Amount = 100m,
            Currency = "USD",
            State = ContributionState.Processing,
            RetryCount = 2,
            Version = 0
        });

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();
        TenantFilterAccessor.Clear();

        return (orgId, contributionId);
    }

    [Fact]
    public async Task CircuitOpen_ShouldNotInvokeProvider()
    {
        var (orgId, contributionId) = await SeedProcessingAsync();
        var clock = new FakeTimeProvider();
        var sp = BuildServices(clock);

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var circuitBreaker = scope.ServiceProvider.GetRequiredService<CircuitBreaker>();
        circuitBreaker.RecordFailure(ErrorCategory.ServerError); // opens the circuit

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CB-001"));

        Assert.Equal(ProviderSubmissionDisposition.DeferredBecauseCircuitOpen, result.Disposition);

        var provider = scope.ServiceProvider.GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        Assert.Equal(0, provider!.OperationCount);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task CircuitOpen_ShouldNotCreateBusinessAttempt()
    {
        var (orgId, contributionId) = await SeedProcessingAsync();
        var sp = BuildServices(new FakeTimeProvider());

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var circuitBreaker = scope.ServiceProvider.GetRequiredService<CircuitBreaker>();
        circuitBreaker.RecordFailure(ErrorCategory.ServerError);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CB-001"));
        Assert.Equal(ProviderSubmissionDisposition.DeferredBecauseCircuitOpen, result.Disposition);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var attempts = await db.Set<ProcessingAttempt>().IgnoreQueryFilters()
            .Where(a => a.ContributionId == contributionId)
            .ToListAsync();
        Assert.Empty(attempts);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task CircuitOpen_ShouldNotIncreaseRetryCount()
    {
        var (orgId, contributionId) = await SeedProcessingAsync();
        var sp = BuildServices(new FakeTimeProvider());

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var circuitBreaker = scope.ServiceProvider.GetRequiredService<CircuitBreaker>();
        circuitBreaker.RecordFailure(ErrorCategory.ServerError);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CB-001"));
        Assert.Equal(ProviderSubmissionDisposition.DeferredBecauseCircuitOpen, result.Disposition);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        // Retry budget must NOT be consumed while the circuit is open.
        Assert.Equal(2, contribution.RetryCount);
        Assert.Equal(ContributionState.Processing, contribution.State);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task CircuitRecovered_SingleProbe_ShouldSubmitAndClose()
    {
        var (orgId, contributionId) = await SeedProcessingAsync();
        var clock = new FakeTimeProvider();
        var sp = BuildServices(clock);

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "test");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var circuitBreaker = scope.ServiceProvider.GetRequiredService<CircuitBreaker>();
        circuitBreaker.RecordFailure(ErrorCategory.ServerError);
        clock.Advance(TimeSpan.FromSeconds(11)); // open window elapses

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new SubmitToProviderCommand(contributionId, orgId, 100m, "USD", "CB-001"));

        // The first caller after the window is granted the single probe.
        Assert.Equal(ProviderSubmissionDisposition.Succeeded, result.Disposition);
        Assert.Equal(CircuitBreakerState.Closed, circuitBreaker.State);

        TenantFilterAccessor.Clear();
    }
}
