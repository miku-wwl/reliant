namespace Reliant.Application.Dto;

/// <summary>
/// Versioned contract for the "processing" queue messages
/// (<c>ContributionCreated</c> and <c>ContributionRetryRequested</c>).
/// Only identity is carried in the payload; all business facts
/// (amount, currency, external reference) are re-read from PostgreSQL
/// by the worker so message payloads can never drift from the database.
/// </summary>
public sealed record ContributionProcessingMessage(
    int Version,
    Guid ContributionId,
    Guid OrganizationId,
    string Trigger,
    string CorrelationId);
