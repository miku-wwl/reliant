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
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp10;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
public sealed class ConcurrentReconciliationTests :
    IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ConcurrentReconciliationTests(
        PostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task TwoReconcilers_ShouldApplyOnlyOneRecoveryAction()
    {
        var startedAt = DateTime.UtcNow;

        await VerifySafeRetryRaceAsync();
        await VerifyTerminalSuccessRaceAsync();

        _output.WriteLine(
            "FINAL | Scenarios=SafeRetry,Succeeded | " +
            "ExecutorsPerScenario=2 | EffectiveActionsPerScenario=1 | " +
            "UnhandledExceptions=0 | RESULT=PASS | " +
            "StartedAt={0:O} | CompletedAt={1:O}",
            startedAt,
            DateTime.UtcNow);
    }

    private async Task VerifySafeRetryRaceAsync()
    {
        const string providerKey =
            "phase3-exp10-safe-retry-key";
        var (organizationId, contributionId) =
            await SeedReconciliationPendingAsync(providerKey);
        var provider = new BarrierQueryProvider(
            new ProviderStatusResult(
                ProviderStatus.NotFound,
                ProviderReference: null,
                ErrorMessage: "Operation not found"));

        var results = await RunTwoReconcilersAsync(
            organizationId,
            contributionId,
            provider);

        Assert.Equal(2, provider.QueryCount);
        Assert.All(results, result => Assert.True(result.Resolved));
        Assert.Single(
            results,
            result => result.Resolution == "SafeRetry");
        Assert.Single(
            results,
            result => result.Resolution ==
                "Concurrent reconciliation already applied");

        await using var db = CreateDbContext();
        var contribution = await db.Contributions
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == contributionId);
        var transitions = await db.StateTransitions
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .ToListAsync();
        var records = await db.ReconciliationRecords
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .ToListAsync();
        var references = await db.ProviderReferences
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .ToListAsync();

        Assert.Equal(
            ContributionState.RetryPending,
            contribution.State);
        Assert.Equal(1, contribution.Version);
        Assert.Equal(0, contribution.RetryCount);
        Assert.NotNull(contribution.NextRetryAt);
        Assert.True(contribution.NextRetryAt > DateTime.UtcNow);
        Assert.Collection(
            transitions,
            transition =>
            {
                Assert.Equal(
                    ContributionState.ReconciliationPending,
                    transition.FromState);
                Assert.Equal(
                    ContributionState.RetryPending,
                    transition.ToState);
                Assert.Equal(
                    "ReconciliationHandler",
                    transition.ChangedBy);
            });
        Assert.Collection(
            records,
            record =>
            {
                Assert.Equal("NotFound", record.ProviderState);
                Assert.Equal(
                    ReconciliationDifference.ProviderNotFound,
                    record.Difference);
                Assert.Equal("SafeRetry", record.Resolution);
                Assert.NotNull(record.ResolvedAt);
                Assert.Equal(
                    "ReconciliationHandler",
                    record.ResolvedBy);
            });
        Assert.Empty(references);
        Assert.Equal(
            0,
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .CountAsync());
        Assert.Equal(
            0,
            await db.DeadLetterRecords
                .IgnoreQueryFilters()
                .CountAsync());

        _output.WriteLine(
            "SAFE RETRY RACE | QueryCount=2 | Results={0},{1} | " +
            "State=RetryPending | Version=1 | Transition=1 | " +
            "Record=1/SafeRetry | NextRetryAt={2:O} | " +
            "ProviderReference=0 | Exceptions=0",
            results[0].Resolution,
            results[1].Resolution,
            contribution.NextRetryAt);
    }

    private async Task VerifyTerminalSuccessRaceAsync()
    {
        const string providerKey =
            "phase3-exp10-terminal-success-key";
        const string providerReference =
            "phase3-exp10-provider-reference";
        var (organizationId, contributionId) =
            await SeedReconciliationPendingAsync(providerKey);
        var provider = new BarrierQueryProvider(
            new ProviderStatusResult(
                ProviderStatus.Succeeded,
                providerReference,
                ErrorMessage: null));

        var results = await RunTwoReconcilersAsync(
            organizationId,
            contributionId,
            provider);

        Assert.Equal(2, provider.QueryCount);
        Assert.All(results, result => Assert.True(result.Resolved));
        Assert.Single(
            results,
            result => result.Resolution == "AutoFixed");
        Assert.Single(
            results,
            result => result.Resolution ==
                "Concurrent reconciliation already applied");

        await using var db = CreateDbContext();
        var contribution = await db.Contributions
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == contributionId);
        var transitions = await db.StateTransitions
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .ToListAsync();
        var records = await db.ReconciliationRecords
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .ToListAsync();
        var references = await db.ProviderReferences
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .ToListAsync();

        Assert.Equal(
            ContributionState.Succeeded,
            contribution.State);
        Assert.Equal(1, contribution.Version);
        Assert.Null(contribution.NextRetryAt);
        Assert.Collection(
            transitions,
            transition =>
            {
                Assert.Equal(
                    ContributionState.ReconciliationPending,
                    transition.FromState);
                Assert.Equal(
                    ContributionState.Succeeded,
                    transition.ToState);
                Assert.Equal(
                    "ReconciliationHandler",
                    transition.ChangedBy);
            });
        Assert.Collection(
            records,
            record =>
            {
                Assert.Equal("Succeeded", record.ProviderState);
                Assert.Equal(
                    ReconciliationDifference.StateMismatch,
                    record.Difference);
                Assert.Equal("AutoFixed", record.Resolution);
                Assert.NotNull(record.ResolvedAt);
                Assert.Equal(
                    "ReconciliationHandler",
                    record.ResolvedBy);
            });
        Assert.Collection(
            references,
            reference =>
            {
                Assert.Equal(
                    providerReference,
                    reference.Reference);
                Assert.Equal("sandbox", reference.ProviderName);
            });
        Assert.Equal(
            0,
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .CountAsync());
        Assert.Equal(
            0,
            await db.DeadLetterRecords
                .IgnoreQueryFilters()
                .CountAsync());

        _output.WriteLine(
            "TERMINAL RACE | QueryCount=2 | Results={0},{1} | " +
            "State=Succeeded | Version=1 | Transition=1 | " +
            "Record=1/AutoFixed | RetrySchedule=0 | " +
            "ProviderReference=1/{2} | Exceptions=0",
            results[0].Resolution,
            results[1].Resolution,
            references[0].Reference);
    }

    private async Task<ReconciliationResult[]> RunTwoReconcilersAsync(
        Guid organizationId,
        Guid contributionId,
        BarrierQueryProvider provider)
    {
        using var services = BuildServices(provider);
        using var scopeA = services.CreateScope();
        using var scopeB = services.CreateScope();
        SetTenant(scopeA.ServiceProvider, organizationId, "worker-a");
        SetTenant(scopeB.ServiceProvider, organizationId, "worker-b");

        var senderA = scopeA.ServiceProvider
            .GetRequiredService<MediatR.ISender>();
        var senderB = scopeB.ServiceProvider
            .GetRequiredService<MediatR.ISender>();

        try
        {
            var taskA = senderA.Send(
                new ReconcileContributionCommand(contributionId));
            var taskB = senderB.Send(
                new ReconcileContributionCommand(contributionId));
            return await Task.WhenAll(taskA, taskB);
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private ServiceProvider BuildServices(IProvider provider)
    {
        var services = new ServiceCollection();
        services.AddMediatR(config =>
            config.RegisterServicesFromAssembly(
                typeof(AssemblyMarker).Assembly));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IContributionRepository,
            ContributionRepository>();
        services.AddScoped<IProcessingAttemptRepository,
            ProcessingAttemptRepository>();
        services.AddScoped<IProviderReferenceRepository,
            ProviderReferenceRepository>();
        services.AddScoped<IReconciliationRepository,
            ReconciliationRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IStateTransitionRepository,
            StateTransitionRepository>();
        services.AddSingleton(provider);
        services.AddSingleton<IProvider>(provider);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<ReliantDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        return services.BuildServiceProvider();
    }

    private async Task<(Guid OrganizationId, Guid ContributionId)>
        SeedReconciliationPendingAsync(string providerKey)
    {
        await _fixture.ResetAsync();
        var db = _fixture.DbContext;
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 3 Experiment 10 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 10",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Contributions.Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = organizationId,
            CampaignId = campaignId,
            ExternalReference = "PHASE3-EXP10-001",
            Amount = 250m,
            Currency = "NZD",
            State = ContributionState.ReconciliationPending,
            Version = 0
        });
        db.ProcessingAttempts.Add(new ProcessingAttempt
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = organizationId,
            AttemptNumber = 1,
            ProviderName = "sandbox",
            ProviderIdempotencyKey = providerKey,
            Status = AttemptStatus.Unknown,
            RequestPayload = "{}"
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (organizationId, contributionId);
    }

    private static void SetTenant(
        IServiceProvider serviceProvider,
        Guid organizationId,
        string correlationId)
    {
        var tenant = serviceProvider
            .GetRequiredService<ITenantContext>();
        tenant.SetTenant(
            organizationId,
            userId: null,
            role: null,
            correlationId);
        TenantFilterAccessor.SetOrganizationId(organizationId);
    }

    private ReliantDbContext CreateDbContext()
    {
        var options =
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(_fixture.ConnectionString)
                .Options;
        return new ReliantDbContext(options);
    }

    private sealed class BarrierQueryProvider(
        ProviderStatusResult result) : IProvider
    {
        private readonly TaskCompletionSource _bothQueriesArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _queryCount;

        public int QueryCount => Volatile.Read(ref _queryCount);

        public Task<ProviderResult> SubmitAsync(
            ProviderRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException(
                "Exp10 only exercises provider status queries.");

        public Task<ProviderStatusResult> QueryStatusByReferenceAsync(
            string providerReference,
            CancellationToken ct = default)
            => WaitForBothQueriesAsync(ct);

        public Task<ProviderStatusResult>
            QueryStatusByIdempotencyKeyAsync(
                string idempotencyKey,
                CancellationToken ct = default)
            => WaitForBothQueriesAsync(ct);

        public Task<ProviderResult> CancelAsync(
            string providerReference,
            CancellationToken ct = default)
            => throw new NotSupportedException(
                "Exp10 does not cancel provider operations.");

        public Task<ProviderHealthResult> CheckHealthAsync(
            CancellationToken ct = default)
            => Task.FromResult(new ProviderHealthResult(true, "Exp10"));

        private async Task<ProviderStatusResult> WaitForBothQueriesAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _queryCount) == 2)
            {
                _bothQueriesArrived.TrySetResult();
            }

            await _bothQueriesArrived.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                cancellationToken);
            return result;
        }
    }
}
