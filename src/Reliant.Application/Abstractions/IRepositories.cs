using Reliant.Domain.Entities;

namespace Reliant.Application.Abstractions;

public interface IContributionRepository
{
    Task<Contribution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Contribution?> GetByIdIgnoreTenantAsync(Guid id, CancellationToken cancellationToken = default);
    Task<(List<Contribution> items, string? nextCursor)> ListAsync(int limit, string? cursor, CancellationToken cancellationToken = default);
    Task AddAsync(Contribution contribution, CancellationToken cancellationToken = default);
    Task UpdateAsync(Contribution contribution, CancellationToken cancellationToken = default);
    Task<List<Contribution>> GetRetryDueAsync(int limit, CancellationToken cancellationToken = default);
}

public interface ICampaignRepository
{
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<Campaign>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Campaign campaign, CancellationToken cancellationToken = default);
}

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(Organization organization, CancellationToken cancellationToken = default);
}

public interface IMembershipRepository
{
    Task<Membership?> GetByOrgAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default);
    Task<List<Membership>> ListByOrgAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task AddAsync(Membership membership, CancellationToken cancellationToken = default);
}

public interface IIdempotencyRepository
{
    Task<IdempotencyRecord?> GetByKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken = default);
}

public interface IStateTransitionRepository
{
    Task AddAsync(StateTransition transition, CancellationToken cancellationToken = default);
    Task<List<StateTransition>> ListByContributionAsync(Guid contributionId, CancellationToken cancellationToken = default);
}

public interface IAuditEventRepository
{
    Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task<List<AuditEvent>> ListByEntityAsync(Guid organizationId, string entityType, Guid entityId, CancellationToken cancellationToken = default);
}
