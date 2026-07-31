using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Messaging;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Infrastructure.Provider;
using Reliant.Infrastructure.Queue;

namespace Reliant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddReliantInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ReliantDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("PostgreSQL") ??
                "Host=localhost;Port=5432;Database=reliant;Username=reliant;Password=reliant-dev"));

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
        services.AddScoped<IJobRunRepository, JobRunRepository>();
        services.AddScoped<ILeaseRepository, LeaseRepository>();
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddScoped<ICheckpointRepository, CheckpointRepository>();

        services.AddScoped<IProcessingAttemptRepository, ProcessingAttemptRepository>();
        services.AddScoped<IProviderReferenceRepository, ProviderReferenceRepository>();
        services.AddScoped<IReconciliationRepository, ReconciliationRepository>();

        services.AddSingleton<IQueueAdapter, SqsQueueAdapter>();
        services.AddSingleton<IQueueMessagePublisher, QueueMessagePublisher>();
        services.AddSingleton<IProvider, SandboxProvider>();
        services.AddSingleton<CircuitBreaker>();

        return services;
    }
}
