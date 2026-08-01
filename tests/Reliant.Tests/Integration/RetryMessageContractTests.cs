using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Dto;
using Reliant.Application.Tenancy;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;
using System.Text.Json;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
public class RetryMessageContractTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public RetryMessageContractTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private IServiceProvider BuildServices()
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
        services.AddSingleton<Reliant.Application.Messaging.CircuitBreaker>();
        services.AddDbContext<ReliantDbContext>(opt => opt.UseNpgsql(_fixture.ConnectionString));

        return services.BuildServiceProvider();
    }

    private async Task<(Guid orgId, Guid campaignId)> SeedOrgAndCampaignAsync()
    {
        await _fixture.ResetAsync();
        var db = _fixture.DbContext;

        var orgId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

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

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();
        TenantFilterAccessor.Clear();

        return (orgId, campaignId);
    }

    [Fact]
    public async Task CreateContribution_ShouldEmitVersionedProcessingContract()
    {
        var (orgId, campaignId) = await SeedOrgAndCampaignAsync();
        var sp = BuildServices();

        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(orgId, null, null, "correlation-1");
        TenantFilterAccessor.SetOrganizationId(orgId);

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var response = await sender.Send(new CreateContributionCommand(
            campaignId, "CONTRACT-001", 100m, "USD", "idem-contract-001"));

        Assert.Equal(201, response.StatusCode);
        Assert.NotNull(response.Body);

        var db = scope.ServiceProvider.GetRequiredService<ReliantDbContext>();
        var outbox = await db.Set<OutboxMessage>().IgnoreQueryFilters()
            .SingleAsync(o => o.MessageType == "ContributionCreated");

        var message = JsonSerializer.Deserialize<ContributionProcessingMessage>(outbox.Payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(message);
        Assert.Equal(1, message!.Version);
        Assert.Equal("Created", message.Trigger);
        Assert.Equal(response.Body!.Id, message.ContributionId);
        Assert.Equal(orgId, message.OrganizationId);
        Assert.Equal("correlation-1", message.CorrelationId);

        // The contract carries only identity - never business facts.
        using var doc = JsonDocument.Parse(outbox.Payload);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("amount", props);
        Assert.DoesNotContain("currency", props);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public void RetryProcessingContract_ShouldCarryOnlyIdentity()
    {
        var message = new ContributionProcessingMessage(
            Version: 1,
            ContributionId: Guid.NewGuid(),
            OrganizationId: Guid.NewGuid(),
            Trigger: "Retry",
            CorrelationId: Guid.NewGuid().ToString());

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Contains("version", props);
        Assert.Contains("contributionId", props);
        Assert.Contains("organizationId", props);
        Assert.Contains("trigger", props);
        Assert.Contains("correlationId", props);
        Assert.DoesNotContain("amount", props);
        Assert.DoesNotContain("currency", props);

        var roundTrip = JsonSerializer.Deserialize<ContributionProcessingMessage>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal(message, roundTrip);
    }

    [Fact]
    public async Task RetryPending_ShouldBeSelectableWhenDueAndTransitionToProcessing()
    {
        var (orgId, campaignId) = await SeedOrgAndCampaignAsync();
        var db = _fixture.DbContext;

        var dueId = Guid.NewGuid();
        var notDueId = Guid.NewGuid();
        var clearedId = Guid.NewGuid();

        db.Set<Contribution>().AddRange(
            new Contribution
            {
                Id = dueId,
                OrganizationId = orgId,
                CampaignId = campaignId,
                ExternalReference = "DUE-1",
                Amount = 10m,
                Currency = "USD",
                State = ContributionState.RetryPending,
                NextRetryAt = DateTime.UtcNow.AddSeconds(-5),
                Version = 0
            },
            new Contribution
            {
                Id = notDueId,
                OrganizationId = orgId,
                CampaignId = campaignId,
                ExternalReference = "NOTDUE-1",
                Amount = 10m,
                Currency = "USD",
                State = ContributionState.RetryPending,
                NextRetryAt = DateTime.UtcNow.AddSeconds(500),
                Version = 0
            },
            new Contribution
            {
                Id = clearedId,
                OrganizationId = orgId,
                CampaignId = campaignId,
                ExternalReference = "CLEARED-1",
                Amount = 10m,
                Currency = "USD",
                State = ContributionState.RetryPending,
                NextRetryAt = null,
                Version = 0
            });

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();
        TenantFilterAccessor.Clear();

        var sp = BuildServices();
        using var scope = sp.CreateScope();
        var contributionRepo = scope.ServiceProvider.GetRequiredService<IContributionRepository>();

        var due = await contributionRepo.GetRetryDueAsync(10, DateTime.UtcNow);
        var dueIds = due.Select(c => c.Id).ToHashSet();

        // Due retries are dispatched; not-due and already-scheduled (NextRetryAt=null) are not.
        Assert.Contains(dueId, dueIds);
        Assert.DoesNotContain(notDueId, dueIds);
        Assert.DoesNotContain(clearedId, dueIds);

        var dueContribution = due.Single(c => c.Id == dueId);
        Assert.True(dueContribution.CanTransitionTo(ContributionState.Processing));
        dueContribution.TransitionTo(ContributionState.Processing, "Retry picked up");
        Assert.Equal(ContributionState.Processing, dueContribution.State);
    }
}
