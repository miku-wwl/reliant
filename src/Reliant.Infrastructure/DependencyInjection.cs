using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;

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

        return services;
    }
}
