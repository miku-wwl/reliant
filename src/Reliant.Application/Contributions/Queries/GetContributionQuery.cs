using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using MediatR;

namespace Reliant.Application.Contributions.Queries;

public record GetContributionQuery(Guid ContributionId) : IRequest<ContributionResponse?>;

public class GetContributionHandler(
    IContributionRepository contributionRepository,
    ITenantContext tenantContext) : IRequestHandler<GetContributionQuery, ContributionResponse?>
{
    public async Task<ContributionResponse?> Handle(GetContributionQuery request, CancellationToken cancellationToken)
    {
        var contribution = await contributionRepository.GetByIdAsync(request.ContributionId, cancellationToken);
        if (contribution is null || contribution.OrganizationId != tenantContext.OrganizationId)
        {
            return null;
        }

        return new ContributionResponse(
            contribution.Id,
            contribution.OrganizationId,
            contribution.CampaignId,
            contribution.ExternalReference,
            contribution.Amount,
            contribution.Currency,
            contribution.State.ToString(),
            contribution.CreatedAt,
            contribution.UpdatedAt,
            contribution.Version);
    }
}

public record ListContributionsQuery(int Limit = 20, string? Cursor = null) : IRequest<ListResponse<ContributionResponse>>;

public class ListContributionsHandler(
    IContributionRepository contributionRepository) : IRequestHandler<ListContributionsQuery, ListResponse<ContributionResponse>>
{
    public async Task<ListResponse<ContributionResponse>> Handle(ListContributionsQuery request, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await contributionRepository.ListAsync(request.Limit, request.Cursor, cancellationToken);
        var responses = items.Select(c => new ContributionResponse(
            c.Id,
            c.OrganizationId,
            c.CampaignId,
            c.ExternalReference,
            c.Amount,
            c.Currency,
            c.State.ToString(),
            c.CreatedAt,
            c.UpdatedAt,
            c.Version)).ToList();

        return new ListResponse<ContributionResponse>(responses, nextCursor);
    }
}
