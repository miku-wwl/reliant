using Microsoft.AspNetCore.Http;
using Reliant.Application.Abstractions;
using Reliant.Application.Observability;
using Reliant.Infrastructure.Persistence;
using System.Diagnostics;

namespace Reliant.Api.Middleware;

public class TenantMiddleware(
    RequestDelegate next,
    ILogger<TenantMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var orgIdStr = context.User.FindFirst("org_id")?.Value;
        var userIdStr = context.User.FindFirst("sub")?.Value;
        var role = context.User.FindFirst("role")?.Value;
        var requestedCorrelationId =
            context.Request.Headers["X-Correlation-ID"]
                .FirstOrDefault();
        var correlationId = IsSafeCorrelationId(
            requestedCorrelationId)
            ? requestedCorrelationId!
            : Activity.Current?.TraceId.ToString() ??
                context.TraceIdentifier;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        Activity.Current?.AddBaggage(
            "reliant.correlation_id",
            correlationId);

        var tenantSafeId = Guid.TryParse(orgIdStr, out var parsedOrgId)
            ? ReliantTelemetry.TenantSafeId(parsedOrgId)
            : "anonymous";
        using var logScope = logger.BeginScope(
            new Dictionary<string, object?>
            {
                ["CorrelationId"] = correlationId,
                ["TenantSafeId"] = tenantSafeId
            });

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

    private static bool IsSafeCorrelationId(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
            value.Length <= 128 &&
            value.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.');
}
