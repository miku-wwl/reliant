using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? ContributionId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
