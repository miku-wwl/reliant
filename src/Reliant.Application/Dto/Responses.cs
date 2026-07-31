namespace Reliant.Application.Dto;

public record CreateContributionRequest(
    Guid CampaignId,
    string ExternalReference,
    decimal Amount,
    string Currency,
    string IdempotencyKey);

public record ContributionResponse(
    Guid Id,
    Guid OrganizationId,
    Guid CampaignId,
    string ExternalReference,
    decimal Amount,
    string Currency,
    string State,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Version);

public record CreateCampaignRequest(
    string Name,
    string? Description);

public record CampaignResponse(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string? Description,
    string Status,
    DateTime CreatedAt,
    int Version);

public record CreateOrganizationRequest(
    string Name,
    string OwnerEmail,
    string OwnerExternalId);

public record OrganizationResponse(
    Guid Id,
    string Name,
    string Status,
    DateTime CreatedAt);

public record ListResponse<T>(List<T> Items, string? NextCursor);

public record IdempotentResponse<T>(int StatusCode, T? Body, bool WasCached);
