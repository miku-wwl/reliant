using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Tenancy;
using MediatR;

namespace Reliant.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddReliantApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(AssemblyMarker).Assembly));
        services.AddScoped<ITenantContext, TenantContext>();
        return services;
    }
}
