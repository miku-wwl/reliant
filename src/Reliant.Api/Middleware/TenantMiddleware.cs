using Microsoft.AspNetCore.Http;
using Reliant.Application.Abstractions;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Api.Middleware;

public class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var orgIdStr = context.User.FindFirst("org_id")?.Value;
        var userIdStr = context.User.FindFirst("sub")?.Value;
        var role = context.User.FindFirst("role")?.Value;
        var correlationId = context.TraceIdentifier;

        if (Guid.TryParse(orgIdStr, out var orgId))
        {
            tenantContext.SetTenant(orgId,
                Guid.TryParse(userIdStr, out var userId) ? userId : null,
                role, correlationId);
            TenantFilterAccessor.SetOrganizationId(orgId);
        }

        try
        {
            await next(context);
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }
}
