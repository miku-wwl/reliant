using Reliant.Domain.Entities;

namespace Reliant.Application.Abstractions;

public interface IAuditService
{
    Task RecordAsync(string entityType, Guid entityId, string action, string changedBy, string correlationId, string? oldValues = null, string? newValues = null, CancellationToken cancellationToken = default);
}
