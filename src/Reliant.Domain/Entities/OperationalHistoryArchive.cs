namespace Reliant.Domain.Entities;

/// <summary>
/// Durable online archive for operational records that must leave hot tables
/// without losing the evidence needed for incident review. A source row is
/// archived at most once.
/// </summary>
public sealed class OperationalHistoryArchive
{
    public Guid Id { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTime SourceOccurredAt { get; set; }
    public DateTime ArchivedAt { get; set; } = DateTime.UtcNow;
    public string Payload { get; set; } = string.Empty;
}
