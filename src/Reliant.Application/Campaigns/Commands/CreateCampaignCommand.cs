using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using MediatR;

namespace Reliant.Application.Campaigns.Commands;

public record CreateCampaignCommand(string Name, string? Description) : IRequest<CampaignResponse>;

public class CreateCampaignHandler(
    ICampaignRepository campaignRepository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext) : IRequestHandler<CreateCampaignCommand, CampaignResponse>
{
    public async Task<CampaignResponse> Handle(CreateCampaignCommand request, CancellationToken cancellationToken)
    {
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            OrganizationId = tenantContext.OrganizationId,
            Name = request.Name,
            Description = request.Description,
            Version = 0
        };

        await campaignRepository.AddAsync(campaign, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CampaignResponse(
            campaign.Id,
            campaign.OrganizationId,
            campaign.Name,
            campaign.Description,
            campaign.Status.ToString(),
            campaign.CreatedAt,
            campaign.Version);
    }
}
