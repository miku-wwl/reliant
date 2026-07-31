using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Tenancy;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;
using Reliant.Worker.Scheduling;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
public class RetrySchedulingTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public RetrySchedulingTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddSingleton<IProviderOperationKeyFactory, ProviderOperationKeyFactory>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IRetryScheduler, RetrySchedulerService>();

        var configDict = new Dictionary<string, string?> { ["Provider:Mode"] = "Success", ["Provider:Secret"] = "test-secret" };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        services.AddSingleton<IProvider, SandboxProvider>();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<Reliant.Application.Messaging.CircuitBreaker>();
        services.AddDbContext<ReliantDbContext>(opt => opt.UseNpgsql(_fixture.ConnectionString));

        return services.BuildServiceProvider();
    }

    private async Task<(Guid orgId, Guid contributionId)> SeedRetryPendingAsync(int retryCount, DateTime? nextRetryAt)
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
            ExternalReference = "RETRY-001",
            Amount = 100m,
            Currency = "USD",
            State = ContributionState.RetryPending,
            RetryCount = retryCount,
            NextRetryAt = nextRetryAt,
            Version = 0
        });

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();
        TenantFilterAccessor.Clear();

        return (orgId, contributionId);
    }

    [Fact]
    public async Task RetryPending_NotDue_ShouldNotBeDispatched()
    {
        var (_, contributionId) = await SeedRetryPendingAsync(0, DateTime.UtcNow.AddMinutes(10));
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IRetryScheduler>();
        var dispatched = await scheduler.DispatchDueRetriesAsync();

        Assert.Equal(0, dispatched);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var outbox = await db.Set<OutboxMessage>().IgnoreQueryFilters()
            .Where(o => o.MessageType == "ContributionRetryRequested")
            .ToListAsync();
        Assert.Empty(outbox);

        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.RetryPending, contribution.State);
    }

    [Fact]
    public async Task RetryPending_Due_ShouldCreateOneOutboxMessage()
    {
        var (orgId, contributionId) = await SeedRetryPendingAsync(2, DateTime.UtcNow.AddSeconds(-30));
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IRetryScheduler>();
        var dispatched = await scheduler.DispatchDueRetriesAsync();

        Assert.Equal(1, dispatched);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var outbox = await db.Set<OutboxMessage>().IgnoreQueryFilters()
            .Where(o => o.MessageType == "ContributionRetryRequested" && o.OrganizationId == orgId)
            .ToListAsync();
        Assert.Single(outbox);

        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        // Stays RetryPending (worker owns the transition) and marked scheduled.
        Assert.Equal(ContributionState.RetryPending, contribution.State);
        Assert.Null(contribution.NextRetryAt);
        Assert.Equal(2, contribution.RetryCount);
    }

    [Fact]
    public async Task ConcurrentSchedulers_ShouldDispatchOnlyOnce()
    {
        var (orgId, _) = await SeedRetryPendingAsync(1, DateTime.UtcNow.AddSeconds(-30));
        var sp1 = BuildServices();
        var sp2 = BuildServices();

        using var scope1 = sp1.CreateScope();
        using var scope2 = sp2.CreateScope();

        var scheduler1 = scope1.ServiceProvider.GetRequiredService<IRetryScheduler>();
        var scheduler2 = scope2.ServiceProvider.GetRequiredService<IRetryScheduler>();

        var results = await Task.WhenAll(
            scheduler1.DispatchDueRetriesAsync(),
            scheduler2.DispatchDueRetriesAsync());

        Assert.Equal(1, results.Sum());

        var db = scope1.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var outbox = await db.Set<OutboxMessage>().IgnoreQueryFilters()
            .Where(o => o.MessageType == "ContributionRetryRequested" && o.OrganizationId == orgId)
            .ToListAsync();

        // Only one retry message may ever be dispatched for the contribution.
        Assert.Single(outbox);
    }

    [Fact]
    public async Task MaxRetryAttempts_ShouldMoveToFailedAndCreateDeadLetter()
    {
        var (orgId, contributionId) = await SeedRetryPendingAsync(5, DateTime.UtcNow.AddSeconds(-30));
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IRetryScheduler>();
        var dispatched = await scheduler.DispatchDueRetriesAsync();

        Assert.Equal(1, dispatched);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Failed, contribution.State);
        Assert.Null(contribution.NextRetryAt);

        var deadLetter = await db.Set<DeadLetterRecord>().IgnoreQueryFilters()
            .Where(d => d.OrganizationId == orgId && d.MessageType == "ContributionRetryExhausted")
            .ToListAsync();
        Assert.Single(deadLetter);

        var alert = await db.Set<OutboxMessage>().IgnoreQueryFilters()
            .Where(o => o.MessageType == "OperatorAlert" && o.OrganizationId == orgId)
            .ToListAsync();
        Assert.Single(alert);
    }

    [Fact]
    public async Task RetryCount_ShouldPersistAcrossDispatch()
    {
        var (_, contributionId) = await SeedRetryPendingAsync(3, DateTime.UtcNow.AddSeconds(-30));
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IRetryScheduler>();
        await scheduler.DispatchDueRetriesAsync();

        // RetryCount is durable in PostgreSQL, not just in worker memory.
        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(3, contribution.RetryCount);
    }
}
