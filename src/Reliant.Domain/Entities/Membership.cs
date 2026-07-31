using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public class Membership
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public MembershipRole Role { get; set; }
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
