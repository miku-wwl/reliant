using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using MediatR;

namespace Reliant.Application.Organizations.Commands;

public record CreateOrganizationCommand(string Name, string OwnerEmail, string OwnerExternalId) : IRequest<OrganizationResponse>;

public class CreateOrganizationHandler(
    IOrganizationRepository organizationRepository,
    IMembershipRepository membershipRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateOrganizationCommand, OrganizationResponse>
{
    public async Task<OrganizationResponse> Handle(CreateOrganizationCommand request, CancellationToken cancellationToken)
    {
        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Status = OrganizationStatus.Active,
            Version = 0
        };

        var user = new User
        {
            Id = Guid.NewGuid(),
            ExternalId = request.OwnerExternalId,
            Email = request.OwnerEmail
        };

        var membership = new Membership
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            UserId = user.Id,
            Role = MembershipRole.Owner,
            Status = MembershipStatus.Active
        };

        await organizationRepository.AddAsync(organization, cancellationToken);
        await membershipRepository.AddAsync(membership, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new OrganizationResponse(
            organization.Id,
            organization.Name,
            organization.Status.ToString(),
            organization.CreatedAt);
    }
}
