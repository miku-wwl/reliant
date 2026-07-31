namespace Reliant.Application.Abstractions;

public interface ITenantContext
{
    Guid OrganizationId { get; }
    Guid? UserId { get; }
    string? Role { get; }
    string? CorrelationId { get; }
    void SetTenant(Guid organizationId, Guid? userId, string? role, string? correlationId);
}
