using Reliant.Application.Abstractions;

namespace Reliant.Application.Tenancy;

public sealed class TenantContext : ITenantContext
{
    public Guid OrganizationId { get; private set; }
    public Guid? UserId { get; private set; }
    public string? Role { get; private set; }
    public string? CorrelationId { get; private set; }

    public void SetTenant(Guid organizationId, Guid? userId, string? role, string? correlationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId cannot be empty", nameof(organizationId));
        }

        OrganizationId = organizationId;
        UserId = userId;
        Role = role;
        CorrelationId = correlationId;
    }
}
