using Microsoft.EntityFrameworkCore;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;

namespace Reliant.Infrastructure.Persistence.Repositories;

public class ContributionRepository(ReliantDbContext db) : IContributionRepository
{
    public async Task<Contribution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.Contributions.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Contribution?> GetByIdIgnoreTenantAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.Contributions.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<(List<Contribution> items, string? nextCursor)> ListAsync(int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        var query = db.Contributions.OrderByDescending(c => c.CreatedAt).AsQueryable();

        if (cursor is not null && Guid.TryParse(cursor, out var cursorId))
        {
            var cursorItem = await db.Contributions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cursorId, cancellationToken);
            if (cursorItem is not null)
            {
                query = query.Where(c => c.CreatedAt < cursorItem.CreatedAt);
            }
        }

        var items = await query.Take(limit + 1).ToListAsync(cancellationToken);
        string? nextCursor = null;

        if (items.Count > limit)
        {
            nextCursor = items[limit - 1].Id.ToString();
            items = items.Take(limit).ToList();
        }

        return (items, nextCursor);
    }

    public async Task AddAsync(Contribution contribution, CancellationToken cancellationToken = default)
    {
        await db.Contributions.AddAsync(contribution, cancellationToken);
    }

    public async Task UpdateAsync(Contribution contribution, CancellationToken cancellationToken = default)
    {
        db.Contributions.Update(contribution);
        await Task.CompletedTask;
    }

    public async Task<List<Contribution>> GetRetryDueAsync(int limit, DateTime now, CancellationToken cancellationToken = default)
    {
        return await db.Contributions
            .IgnoreQueryFilters()
            .Where(c => c.State == ContributionState.RetryPending && c.NextRetryAt != null && c.NextRetryAt <= now)
            .OrderBy(c => c.NextRetryAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> ClaimRetryDueAsync(Guid contributionId, DateTime now, CancellationToken cancellationToken = default)
    {
        // Atomic claim: only one concurrent scheduler can set NextRetryAt to null
        // (the row is still due, not yet claimed). The second contender updates
        // 0 rows and must back off, preventing duplicate retry dispatch.
        return await db.Contributions
            .IgnoreQueryFilters()
            .Where(c => c.Id == contributionId
                && c.State == ContributionState.RetryPending
                && c.NextRetryAt != null
                && c.NextRetryAt <= now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(c => c.NextRetryAt, (DateTime?)null),
                cancellationToken);
    }
}

public class CampaignRepository(ReliantDbContext db) : ICampaignRepository
{
    public async Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<List<Campaign>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await db.Campaigns.OrderByDescending(c => c.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Campaign campaign, CancellationToken cancellationToken = default)
    {
        await db.Campaigns.AddAsync(campaign, cancellationToken);
    }
}

public class OrganizationRepository(ReliantDbContext db) : IOrganizationRepository
{
    public async Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task AddAsync(Organization organization, CancellationToken cancellationToken = default)
    {
        await db.Organizations.AddAsync(organization, cancellationToken);
    }
}

public class MembershipRepository(ReliantDbContext db) : IMembershipRepository
{
    public async Task<Membership?> GetByOrgAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default)
    {
        return await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, cancellationToken);
    }

    public async Task<List<Membership>> ListByOrgAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await db.Memberships.Where(m => m.OrganizationId == organizationId).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Membership membership, CancellationToken cancellationToken = default)
    {
        await db.Memberships.AddAsync(membership, cancellationToken);
    }
}

public class IdempotencyRepository(ReliantDbContext db) : IIdempotencyRepository
{
    public async Task<IdempotencyRecord?> GetByKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        return await db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.OrganizationId == organizationId && r.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        await db.IdempotencyRecords.AddAsync(record, cancellationToken);
    }
}

public class StateTransitionRepository(ReliantDbContext db) : IStateTransitionRepository
{
    public async Task AddAsync(StateTransition transition, CancellationToken cancellationToken = default)
    {
        await db.StateTransitions.AddAsync(transition, cancellationToken);
    }

    public async Task<List<StateTransition>> ListByContributionAsync(Guid contributionId, CancellationToken cancellationToken = default)
    {
        return await db.StateTransitions
            .IgnoreQueryFilters()
            .Where(s => s.ContributionId == contributionId)
            .OrderBy(s => s.ChangedAt)
            .ToListAsync(cancellationToken);
    }
}

public class AuditEventRepository(ReliantDbContext db) : IAuditEventRepository
{
    public async Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await db.AuditEvents.AddAsync(auditEvent, cancellationToken);
    }

    public async Task<List<AuditEvent>> ListByEntityAsync(Guid organizationId, string entityType, Guid entityId, CancellationToken cancellationToken = default)
    {
        return await db.AuditEvents
            .Where(a => a.OrganizationId == organizationId && a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.ChangedAt)
            .ToListAsync(cancellationToken);
    }
}
